using Automha.L2.Plugin.Abstraction;
using Automha.Warehouse.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LaComarca.Plugin.Workers;
namespace LaComarca.Plugin
{
    public class LaComarcaCore : ICorePlugin
    {
        public IServiceCollection ServiceDescriptors { get; }
            = new ServiceCollection()




                .AddHostedService<SynchronizeArticlesWorker>()
                .AddSingleton<IBackendFunctions, LaComarcaBackend>()
                .AddSingleton<IStepProvider, GetLoadingUnitContentStepProvider>();
    }
}
