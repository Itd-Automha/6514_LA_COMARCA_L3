using Automha.Common.Utilities;
using Automha.Infrastructure.Abstractions;
using Automha.Infrastructure.Primitives;
using Automha.Infrastructure.Repositories;
using Automha.L2.Core.Communication.APComm;
using Automha.L2.Core.Communication.APComm.Messages;
using Automha.Warehouse.Abstractions;
using Automha.Warehouse.Extensions;
using LaComarca.Plugin.Lu;
using LaComarca.Plugin.Articles;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LaComarca.Plugin
{
    public class GetLoadingUnitContentsStep : ILoadingUnitStep, IRejectableStep, IDisposable
    {
        private CancellationTokenSource? _cts;
        private Dictionary<string, string> _errors = new();

        private readonly IServiceProvider _services;
        private readonly Lazy<IMissionManager> _mm;
        private readonly CultureInfo _cultureInfoIt = new("it-IT");

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        #region  QUERY
        private const string _getLuBills = "SELECT " +
                                         " [ID_BILL]" +
                                         ",[SSCC]" +
                                         ",[ID_REFERENCE]" +
                                         ",[QT]" +
                                         ",[BATCH_LOT]" +
                                         ",[EXPIRY_DATE]" +
                                         ",[SYNC]" +
                                         ",[SYNC_DATE]" +
                                     "FROM[LA_COMARCA_L3].[dbo].[Lu_Bill]";

        private const string _getLuBill = _getLuBills + "WHERE SSCC = @Barcode and SYNC <> 2";

        private const string _getReferences = "SELECT [ID_REFERENCE]" +
                                               ",[DESCRIPTION]" +
                                               ",[ROTATION_CLASS]" +
                                               ",[SYNC]" +
                                               ",[SYNC_DATE]" +
                                               ",[FLOOR_SELECTION]" +
                                               "FROM[LA_COMARCA_L3].[dbo].[References]";

        private const string _getReference = _getReferences + "WHERE Id_Reference = @Article";

        private const string _updateLuSyncCorrect = "UPDATE Lu_Bill set SYNC=3 WHERE SSCC=@Barcode";
        private const string _updateRefSyncCorrect = "UPDATE [dbo].[References] set SYNC=2, [SYNC_DATE]=GETDATE() WHERE [ID_REFERENCE]=@Barcode";
        private const string _updateRefSyncError = "UPDATE [dbo].[References] set SYNC=-2, [SYNC_DATE]=GETDATE() WHERE [ID_REFERENCE]=@Barcode";


        private const string _ConnectionString = "Data Source=.\\SQLEXPRESS;Integrated Security=true;Connect Timeout=30;Initial Catalog=LA_COMARCA_L3; user id=sa;password=Cst03211030162;Persist Security Info=False;MultipleActiveResultSets=True";
        #endregion


        public GetLoadingUnitContentsStep(ILoadingUnitMission mission, int stepId, IServiceProvider services, StepStatus status = StepStatus.New, string? descr = null, ILink? link = null, ICompoundLink? compoundLink = null, StepWeight stepWeight = default)
        {
            LoadingUnitMission = mission;
            Id = stepId;
            Status = status;
            Description = descr ?? nameof(GetLoadingUnitContentsStep);
            Link = link;
            CompoundLink = compoundLink;
            Priority = mission.Priority;
            StepWeight = stepWeight;
            _services = services;
            _mm = new Lazy<IMissionManager>(() => services.GetRequiredService<IMissionManager>(), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public int Id { get; }

        public string Description { get; }

        public IMission Mission => LoadingUnitMission;

        public StepStatus Status { get; private set; }

        public MessageType MessageTypeId => MessageType.None;

        public ILoadingUnitMission LoadingUnitMission { get; }

        public DateTime? LatestExecution { get; set; }

        public TimeSpan? TotalExecutionTime { get; }

        public ILink? Link { get; }

        public ICompoundLink? CompoundLink { get; }

        public Priority Priority { get; set; }

        public StepWeight StepWeight { get; }

        public bool CheckStartCondition(IEnumerable<IMission> missions)
            => true;

        public StepStatus Resetting()
        {
            CancelAndReset();

            return StepStatus.New;
        }

        public StepStatus Completing(IDataCluster? message = null)
        {
            if (message is not null)
                throw new InvalidOperationException($"Cannot complete {nameof(GetLoadingUnitContentsStep)} with {message.Message.GetType()}");

            CancelAndReset();

            if (_errors.Any())
                return StepStatus.Error;

            return StepStatus.Completed;
        }

        public StepStatus Executing()
        {
            if (_errors.Any())
                _errors = new();

            _cts = new CancellationTokenSource(Timeout);
            _ = GetContentsAsync(_cts.Token).ConfigureAwait(false);

            return StepStatus.Running;
        }
        private void CompleteSelf()
         => _mm.Value.CompleteStep(Mission.Id, Id);

        public async Task GetContentsAsync(CancellationToken token = default)
        {
            try
            {
                if (await IsLuFilledAsync(token))
                {
                    _errors.Add("Contents", "Lu already filled");
                    return;
                }

                var barcode = LoadingUnitMission.LoadingUnit.Data.Code;

                if (string.IsNullOrEmpty(barcode))
                {
                    _errors.Add("Barcode", "Invalid barcode");
                    return;
                }

                List<LoadingUnitContentData> LuBills = new List<LoadingUnitContentData>();

                using (SqlConnection connection = new SqlConnection(_ConnectionString))
                {
                    await connection.OpenAsync(token);
                    using (SqlCommand cmd = new SqlCommand(_getLuBill, connection))
                    {
                        cmd.Parameters.AddWithValue("@Barcode", barcode);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                LoadingUnitContentData LuData = new LoadingUnitContentData();
                                LuData.Id = reader.GetInt32(0);
                                LuData.SSCC = reader.GetString(1);
                                LuData.Id_reference = reader.GetString(2);
                                LuData.QT = reader.GetDecimal(3);
                                LuData.batch = reader.GetString(4);
                                if (!reader.IsDBNull(5))
                                    LuData.Expiry_Date = reader.GetDateTime(5);
                                LuBills.Add(LuData);
                            }
                        }
                    }
                    await connection.CloseAsync();
                }

                await SaveContentsAsync(LuBills , token);

                //metto sync = 3 su lu_bill
                if (!_errors.Any())
                {
                    using (SqlConnection connection = new SqlConnection(_ConnectionString))
                    {
                        await connection.OpenAsync(token);
                        using (SqlCommand cmd = new SqlCommand(_updateLuSyncCorrect, connection))
                        {
                            cmd.Parameters.AddWithValue("@Barcode", barcode);
                            cmd.ExecuteNonQuery();
                        }
                        await connection.CloseAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _errors.Add(ex.GetType().Name, ex.Message);
            }
            finally
            {
                CompleteSelf();
            }
        }

        private async Task SaveContentsAsync(List<LoadingUnitContentData> Data, CancellationToken token)
        {
            await using var uow = _services.GetRequiredService<IUnitOfWork>();

            foreach (var item in Data)
            {
                var article = await uow.ArticleRepository.GetAsync(item.Id_reference, token);
                
                if (article is null)
                {
                    article = await GetArticle(item.Id_reference, uow, token);
                    if (article is null)
                    {
                        _errors.Add("Articolo", "Articolo non trovato:  " + item.Id_reference);
                        break;
                    }
                }

                var _cultureIfoIt = CultureInfo.GetCultureInfo("it-IT");


                if (item.Expiry_Date is null)
                {
                    _errors.Add("Scadenza", "Data scadenza non valida");
                }

                Batch? batch = await GetBatch(Data, uow, item, article, token);
                

                var content = new LoadingUnitContent
                (
                    0,
                    LoadingUnitMission.LoadingUnit.Id,
                    article.Id,
                    item.QT,
                    DateTime.Now,
                    null,
                    item.Expiry_Date,
                    batch?.Id,
                    batch?.Id,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null
                );

                content = await uow.LoadingUnitContentRepository.AddAsync(content, nameof(GetLoadingUnitContentsStep), StockMovementType.Filling, token);

            }
            await uow.SaveChangesAsync(token);
        }

        private static async Task<Batch?> GetBatch(List<LoadingUnitContentData> Data, IUnitOfWork uow, LoadingUnitContentData item, Article? article, CancellationToken token)
        {
            if (!string.IsNullOrEmpty(item.batch))
            {
                var batch = new Batch
                (
                    0,
                    item.batch + " | " + article.Id,
                    article.Id,
                    BatchType.Storage,
                    BatchStatus.New,
                    DateTime.Now,
                    Size: 1 
                );

                return await uow.BatchRepository.GetOrAddAsync(batch, token);
            }

            return null;
        }

        private async Task<Article?> GetArticle(string referenceId, IUnitOfWork uow, CancellationToken token)
        {

            using (SqlConnection connection = new SqlConnection(_ConnectionString))
            {
                if (!string.IsNullOrEmpty(referenceId))
                {
                    ReferenceData? Ref = null;
                    await connection.OpenAsync(token);
                    using (SqlCommand cmd = new SqlCommand(_getReference, connection))
                    {
                        cmd.Parameters.AddWithValue("@Article", referenceId);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Ref = new ReferenceData();
                                Ref.Id_Reference = reader.GetString(0);
                                Ref.Description = reader.GetString(1);
                                Ref.Rotation_Class = reader.GetInt32(2);
                                Ref.Floor_Selection = reader.GetInt32(5);
                            }
                        }
                    }
                    await connection.CloseAsync();

                    if (Ref is not null) 
                    {
                        if (string.IsNullOrEmpty(Ref.Description))
                        {
                            _errors.Add("Articolo", "Descrizione articolo non valida:  " + referenceId);
                            //update sync -2

                            using (SqlConnection Conn = new SqlConnection(_ConnectionString))
                            {
                                await connection.OpenAsync(token);
                                using (SqlCommand cmd = new SqlCommand(_updateRefSyncError, connection))
                                {
                                    cmd.Parameters.AddWithValue("@Barcode", referenceId);
                                    cmd.ExecuteNonQuery();
                                }
                                await connection.CloseAsync();
                            }
                            return null;
                        }
                        var Art = new Article
                        (
                            0,
                            Ref.Id_Reference,
                            Ref.Description,
                            null,
                            null,
                            null,
                            null,
                            null
                        );
                        var result = await uow.ArticleRepository.AddAsync(Art, token);
                        await uow.SaveChangesAsync();

                        if (result is not null)
                        {
                            //sync=2
                            using (SqlConnection Conn = new SqlConnection(_ConnectionString))
                            {
                                await connection.OpenAsync(token);
                                using (SqlCommand cmd = new SqlCommand(_updateRefSyncCorrect, connection))
                                {
                                    cmd.Parameters.AddWithValue("@Barcode", referenceId);
                                    cmd.ExecuteNonQuery();
                                }
                                await connection.CloseAsync();
                            }
                            return result;
                        }
                        else
                        {
                            //sync=-2
                            using (SqlConnection Conn = new SqlConnection(_ConnectionString))
                            {
                                await connection.OpenAsync(token);
                                using (SqlCommand cmd = new SqlCommand(_updateRefSyncError, connection))
                                {
                                    cmd.Parameters.AddWithValue("@Barcode", referenceId);
                                    cmd.ExecuteNonQuery();
                                }
                                await connection.CloseAsync();
                            }
                        }
                    }
                }

                return null;
            }
        }

            public async Task<bool> IsLuFilledAsync(CancellationToken token = default)
            {
                await using var uow = _services.GetRequiredService<IUnitOfWork>();
                var contents = await uow.LoadingUnitContentRepository.GetLoadingUnitContentsAsync(LoadingUnitMission.LoadingUnit.Id, token);         //.ToListAsync(token);
                return contents.Any();
            }

        public StepStatus SetStatus(StepStatus status)
        {
            CancelAndReset();

            Status = status;

            return Status;
        }

        public void Dispose()
        {
            CancelAndReset();
        }

        private void CancelAndReset()
        {
            if (_cts is not null)
            {
                _cts.Dispose();
                _cts = null;
            }
        }

        public IReadOnlyDictionary<string, string> FailureReasons { get => _errors; }

    }
}
