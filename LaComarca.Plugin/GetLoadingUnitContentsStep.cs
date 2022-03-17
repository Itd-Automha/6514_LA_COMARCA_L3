//using Automha.Common.Utilities;
//using Automha.Infrastructure.Abstractions;
//using Automha.Infrastructure.Primitives;
//using Automha.Infrastructure.Repositories;
//using Automha.L2.Core.Communication.APComm;
//using Automha.L2.Core.Communication.APComm.Messages;
//using Automha.Warehouse.Abstractions;
//using Automha.Warehouse.Extensions;
//using Microsoft.Extensions.DependencyInjection;
//using Services;
//using System;
//using System.Collections.Generic;
//using System.Globalization;
//using System.Linq;
//using System.Text;
//using System.Threading;
//using System.Threading.Tasks;

//namespace LaComarca.Plugin
//{
//    public class GetLoadingUnitContentsStep : ILoadingUnitStep, IRejectableStep, IDisposable
//    {
//        private CancellationTokenSource? _cts;
//        private Dictionary<string, string> _errors = new();

//        private readonly IServiceProvider _services;
//        private readonly Lazy<IMissionManager> _mm;
//        private readonly CultureInfo _cultureInfoIt = new("it-IT");

//        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

//        public GetLoadingUnitContentsStep(ILoadingUnitMission mission, int stepId, IServiceProvider services, StepStatus status = StepStatus.New, string? descr = null, ILink? link = null, ICompoundLink? compoundLink = null)
//        {
//            LoadingUnitMission = mission;
//            Id = stepId;
//            Status = status;
//            Description = descr ?? nameof(GetLoadingUnitContentsStep);
//            Link = link;
//            CompoundLink = compoundLink;

//            _services = services;
//            _mm = new Lazy<IMissionManager>(() => services.GetRequiredService<IMissionManager>(), LazyThreadSafetyMode.ExecutionAndPublication);
//        }

//        public int Id { get; }

//        public string Description { get; }

//        public IMission Mission => LoadingUnitMission;

//        public StepStatus Status { get; private set; }

//        public MessageType MessageTypeId => MessageType.None;

//        public ILoadingUnitMission LoadingUnitMission { get; }

//        public DateTime? LatestExecution { get; set; }

//        public TimeSpan? TotalExecutionTime { get; }

//        public ILink? Link { get; }

//        public ICompoundLink? CompoundLink { get; }


//        public bool CheckStartCondition(IEnumerable<IMission> missions)
//            => true;

//        public StepStatus Resetting()
//        {
//            CancelAndReset();

//            return StepStatus.New;
//        }

//        public StepStatus Completing(IDataCluster? message = null)
//        {
//            if (message is not null)
//                throw new InvalidOperationException($"Cannot complete {nameof(GetLoadingUnitContentsStep)} with {message.Message.GetType()}");

//            CancelAndReset();

//            if (_errors.Any())
//                return StepStatus.Error;

//            return StepStatus.Completed;
//        }

//        public StepStatus Executing()
//        {
//            if (_errors.Any())
//                _errors = new();

//            _cts = new CancellationTokenSource(Timeout);
//            _ = GetContentsAsync(_cts.Token).ConfigureAwait(false);

//            return StepStatus.Running;
//        }
//        private void CompleteSelf()
//         => _mm.Value.CompleteStep(Mission.Id, Id);

//        public async Task GetContentsAsync(CancellationToken token = default)
//        {
//            try
//            {
//                if (await IsLuFilledAsync(token))
//                {
//                    _errors.Add("Contents", "Lu already filled");
//                    return;
//                }

//                var barcode = LoadingUnitMission.LoadingUnit.Data.Code;

//                if (string.IsNullOrEmpty(barcode))
//                {
//                    _errors.Add("Barcode", "Invalid barcode");
//                    return;
//                }

//                var req = new GetLuContentReq
//                {
//                    Barcode = barcode
//                };

//                var cli = new SrvReplicaSoapClient(new SrvReplicaSoapClient.EndpointConfiguration());

//                await cli.OpenAsync();

//                var resp = await cli.GetLuContentAsync(req);
//                LoggerObjectToFile.Log("getLuContent " + barcode + " " + resp.Body.GetLuContentResult.EsitoSS);

//                token.ThrowIfCancellationRequested();

//                var res = resp.Body.GetLuContentResult;

//                if (res.EsitoSS != "OK")
//                {
//                    _errors.Add("Esito", "Esito inaspettato da Stocksystem: " + res.EsitoSS);
//                    return;
//                }

//                if (res.Head.UDC != barcode)
//                {
//                    _errors.Add("Risposta", "Risposta inaspettata da Stocksystem: " + res.Head.UDC);
//                    return;
//                }

//                if (res.Body.Length == 0)
//                {
//                    _errors.Add("Contenuto", "Nessun contenuto rilevato");
//                    return;
//                }

//                var contents = await SaveContentsAsync(res, token);

//                string l3Status = _errors.Any() ? "Rejected" : "Accepted";

//                if (l3Status == "Accepted")
//                {
//                    var changeReq = new OnLuStatusChangeReq
//                    {
//                        UDC = LoadingUnitMission.LoadingUnit.Data.Code,
//                        ASI = LoadingUnitMission.Destination.Partition.GetItem().GetAsiCode(),
//                        Stato = l3Status
//                    };

//                    var changeResp = await cli.OnLuStatusChangeAsync(changeReq);

//                    if (changeResp.Body.OnLuStatusChangeResult.EsitoSS != "OK")
//                    {
//                        _errors.Add("Risposta", "Risposta inaspettata da Stocksystem sullo Status change: " + changeResp.Body.OnLuStatusChangeResult.EsitoSS);
//                        return;
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                _errors.Add(ex.GetType().Name, ex.Message);
//            }
//            finally
//            {
//                CompleteSelf();
//            }
//        }

//        private async Task<IEnumerable<LoadingUnitContent>> SaveContentsAsync(GetLuContentRes resp, CancellationToken token)
//        {
//            await using var uow = _services.GetRequiredService<IUnitOfWork>();

//            var compartments = new List<Compartment>();

//            var contents = new List<LoadingUnitContent>(resp.Body.Length);

//            foreach (var item in resp.Body)
//            {
//                var article = await uow.ArticleRepository.GetAsync(item.Articolo, token);

//                if (article is null)
//                {
//                    _errors.Add("Articolo", "Articolo non trovato:  " + item.Articolo);
//                    break;
//                }

//                var _cultureIfoIt = CultureInfo.GetCultureInfo("it-IT");
//                var quantity = decimal.Parse(item.Qta, _cultureInfoIt);

//                DateTime? insertDate = null;

//                if (!string.IsNullOrEmpty(item.DtIngresso))
//                {
//                    if (!DateTime.TryParse(item.DtIngresso, _cultureIfoIt, DateTimeStyles.None, out var parsedDtIngresso))
//                    {
//                        _errors.Add("DtIngresso", "Formato data ingresso non valido:  " + item.DtIngresso);
//                        break;
//                    }
//                    else insertDate = parsedDtIngresso;
//                }

//                DateTime? expirationDate = null;
//                if (!string.IsNullOrEmpty(item.dtScadenza))
//                {
//                    if (!DateTime.TryParse(item.dtScadenza, _cultureIfoIt, DateTimeStyles.None, out var parsedDtScadenza))
//                    {
//                        _errors.Add("DtIngresso", "Formato data scadenza non valido:  " + item.dtScadenza);
//                        break;
//                    }
//                    else expirationDate = parsedDtScadenza;
//                }

//                Batch? batch = await GetBatch(resp, uow, item, article, token);
//                Compartment? compartment;
//                Company? supplier;

//                if (!string.IsNullOrEmpty(item.IdContenitore))
//                {
//                    compartment = compartments.FirstOrDefault(c => c.Code == item.IdContenitore);

//                    if (compartment is null)
//                    {
//                        compartment = new Compartment(0, item.IdContenitore, LoadingUnitMission.LoadingUnit.Id, Jolly1: item.Var1, Jolly2: item.Var2, Jolly3: item.NumeroOrdine, Locked: item.Bloccato.ToLower() == "true");

//                        compartment = await uow.CompartmentRepository.AddAsync(compartment, token);

//                        compartments.Add(compartment);
//                    }
//                }
//                else compartment = null;

//                if (!string.IsNullOrEmpty(item.RagSocFor))
//                {
//                    supplier = new Company(0, null, item.RagSocFor, item.RagSocFor, false, true);

//                    supplier = await uow.CompanyRepository.GetOrAddAsync(supplier, token);
//                }
//                else supplier = null;

//                var content = new LoadingUnitContent
//                (
//                    0,
//                    LoadingUnitMission.LoadingUnit.Id,
//                    article.Id,
//                    quantity,
//                    insertDate ?? DateTime.Now,
//                    null,
//                    expirationDate,
//                    batch?.Id,
//                    batch?.Id,
//                    null,
//                    item.CodRiserva,
//                    null,
//                    supplier?.Id,
//                    compartment?.Id,
//                    null,
//                    null,
//                    false,
//                    null
//                );

//                content = await uow.LoadingUnitContentRepository.AddAsync(content, nameof(GetLoadingUnitContentsStep), StockMovementType.Filling, token);
//                contents.Add(content);

//            }
//            await uow.SaveChangesAsync(token);

//            return contents;
//        }

//        private static async Task<Batch?> GetBatch(GetLuContentRes resp, IUnitOfWork uow, GetLuContentResBody item, Article? article, CancellationToken token)
//        {
//            if (!string.IsNullOrEmpty(item.Lotto))
//            {
//                var batch = new Batch
//                (
//                    0,
//                    item.Lotto + " | " + article.Id,
//                    article.Id,
//                    BatchType.Storage,
//                    BatchStatus.New,
//                    DateTime.Now,
//                    Size: int.Parse(resp.Head.NRPalOmogenei) + 1 //Because NRPalOmogenei not consider itself
//                );

//                return await uow.BatchRepository.GetOrAddAsync(batch, token);
//            }

//            return null;
//        }

//        public async Task<bool> IsLuFilledAsync(CancellationToken token = default)
//        {
//            await using var uow = _services.GetRequiredService<IUnitOfWork>();

//            var contents = await uow.LoadingUnitContentRepository
//                                    .GetLoadingUnitContentsAsync(LoadingUnitMission.LoadingUnit.Id, token)
//                                    .ToListAsync(token);

//            return contents.Any();
//        }

//        public StepStatus SetStatus(StepStatus status)
//        {
//            CancelAndReset();

//            Status = status;

//            return Status;
//        }

//        public void Dispose()
//        {
//            CancelAndReset();
//        }

//        private void CancelAndReset()
//        {
//            if (_cts is not null)
//            {
//                _cts.Dispose();
//                _cts = null;
//            }
//        }

//        public IReadOnlyDictionary<string, string> FailureReasons { get => _errors; }
//    }
//}
