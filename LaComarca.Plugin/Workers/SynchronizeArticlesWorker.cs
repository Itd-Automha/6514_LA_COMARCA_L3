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

        private const string _updateLuSyncCorrect = "UPDATE Lu_Bill set SYNC=3 WHERE SSCC=@Barcode";
        private const string _updateRefSyncCorrect = "UPDATE References set SYNC=2 WHERE SSCC=@Barcode";
        private const string _updateRefSyncError = "UPDATE References set SYNC=-2 WHERE SSCC=@Barcode";
        public SynchronizeArticlesWorker(ILogger<SynchronizeArticlesWorker> logger, IConfiguration configuration, IServiceProvider services)
            : base(logger, configuration)
        {
            _services = services;
            _connString = _cfg.GetValue<string>("L3");
        }
        protected override async Task DoWorkAsync(CancellationToken token = default)
        {
            await using var uow  = _services.GetRequiredService<IUnitOfWork>();
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
                            Ref = new ReferenceData();
                            Ref.Id_Reference = reader.GetString(0);
                            Ref.Description = reader.GetString(1);
                            Ref.Rotation_Class = reader.GetInt32(2);
                            Ref.SYNC = reader.GetInt32(3);
                            Ref.SYNC_Date = reader.GetString(4);
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

                            var result= uow.ArticleRepository.AddOrUpdateAsync(Art, token);
                            if (result is null)
                            {
                                //sync=-2
                                using (SqlCommand cmd2 = new SqlCommand(_updateRefSyncError, connection))
                                {
                                    cmd2.Parameters.AddWithValue("@Barcode", Ref.Id_Reference);
                                }
                                await connection.CloseAsync();
                            }
                            else
                            {
                                //sync=2
                                using (SqlCommand cmd2 = new SqlCommand(_updateRefSyncCorrect, connection))
                                {
                                    cmd2.Parameters.AddWithValue("@Barcode", Ref.Id_Reference);
                                }
                            }
                                await connection.CloseAsync();
                        }
                    }
                }

               
                await connection.CloseAsync();
            
            }
        }
    }
}
