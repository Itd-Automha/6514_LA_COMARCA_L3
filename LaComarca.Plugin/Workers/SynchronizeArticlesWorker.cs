using Automha.Common.Utilities.HostedServices;
using Automha.Infrastructure.Abstractions;
using Automha.Infrastructure.Primitives;
using Automha.Infrastructure.Repositories;
using Automha.Warehouse.Abstractions;
using LaComarca.Plugin.Articles;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LaComarca.Plugin.Workers
{
    public class SynchronizeArticlesWorker : TimerWorker<SynchronizeArticlesWorker>
    {
        private readonly IServiceProvider _services;
        private string _connString;
        private const string _getReferencesSync1 = "SELECT [ID_REFERENCE]" +
                                                   ",[DESCRIPTION]" +
                                                   ",[ROTATION_CLASS]" +
                                                   ",[SYNC]" +
                                                   ",[SYNC_DATE]" +
                                                   ",[FLOOR_SELECTION]" +
                                                   "FROM[LA_COMARCA_L3].[dbo].[References]"+
                                                   "WHERE SYNC=1";

        private const string _updateRefSyncCorrect  = "UPDATE [dbo].[References] set SYNC=2, [SYNC_DATE]=GETDATE() WHERE [ID_REFERENCE]=@Barcode";
        private const string _updateRefSyncError    = "UPDATE [dbo].[References] set SYNC=-2, [SYNC_DATE]=GETDATE() WHERE [ID_REFERENCE]=@Barcode";
        public SynchronizeArticlesWorker(ILogger<SynchronizeArticlesWorker> logger, IConfiguration configuration, IServiceProvider services)
            : base(logger, configuration)
        {
            _services = services;
            _connString = _cfg.GetValue<string>("L3");
        }
        protected override async Task DoWorkAsync(CancellationToken token = default)
        {

            using (SqlConnection connection = new SqlConnection(_connString))
            {
               ReferenceData? Ref = null;
               await connection.OpenAsync(token);
                using (SqlCommand cmd = new SqlCommand(_getReferencesSync1, connection))
                {
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            try
                            {
                                Ref = new ReferenceData();
                                Ref.Id_Reference = reader.GetString(0);
                                Ref.Description = reader.GetString(1);
                                if (!reader.IsDBNull(2))
                                    Ref.Rotation_Class = reader.GetInt32(2);
                                if (!reader.IsDBNull(5))
                                    Ref.Floor_Selection = reader.GetInt32(5);

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

                                int? result = null;
                                if (!string.IsNullOrEmpty(Art.Description))
                                {
                                    result = await AddOrUpdateArticle(Art, token);
                                }

                                if (result is null)
                                {
                                    //Aggiungo alla lista di articoli che avranno sync=-2
                                    using (SqlCommand cmd2 = new SqlCommand(_updateRefSyncError, connection))
                                    {
                                        cmd2.Parameters.AddWithValue("@Barcode", Ref.Id_Reference);
                                        cmd2.Parameters.AddWithValue("@SyncDate", DateTime.Now);
                                        cmd2.ExecuteNonQuery();
                                    }
                                    _log.LogDebug("Error on article: " + Ref.Id_Reference);
                                }
                                else
                                {
                                    //Aggiungo alla lista di articoli che avranno sync=2
                                    using (SqlCommand cmd2 = new SqlCommand(_updateRefSyncCorrect, connection))
                                    {
                                        cmd2.Parameters.AddWithValue("@Barcode", Ref.Id_Reference);
                                        cmd2.Parameters.AddWithValue("@SyncDate", DateTime.Now);
                                        cmd2.ExecuteNonQuery();
                                    }

                                    _log.LogDebug("Added article: " + Ref.Id_Reference);
                                }
                            }
                            catch(Exception ex)
                            {
                                using (SqlCommand cmd2 = new SqlCommand(_updateRefSyncError, connection))
                                {
                                    cmd2.Parameters.AddWithValue("@Barcode", Ref.Id_Reference);
                                    cmd2.Parameters.AddWithValue("@SyncDate", DateTime.Now);
                                    cmd2.ExecuteNonQuery();
                                }
                                _log.LogError(ex.Message);
                            }
                        }
                    }
                }
                    await connection.CloseAsync();
            }
        }

        private async Task<int?> AddOrUpdateArticle(Article A, CancellationToken token = default) 
        {
            try
            {
                await using var uow = _services.GetRequiredService<IUnitOfWork>();
                var Art = await uow.ArticleRepository.GetAsync(A.Code, token);
                if (Art is null)
                {
                    await uow.ArticleRepository.AddAsync(A, token);
                }
                else
                {
                    Art = Art with
                    {
                        Description = A.Description
                    };
                    await uow.ArticleRepository.UpdateAsync(Art);
                }

                await uow.SaveChangesAsync(token);

                return Art?.Id;
            }
            catch (Exception ex) 
            {
                _log.LogError(ex.Message);
                return null;
            }
           
        }
    }
}
