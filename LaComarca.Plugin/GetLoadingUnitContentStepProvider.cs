//using Automha.Warehouse.Abstractions;
//using System;
//using System.Linq;

//namespace LaComarca.Plugin
//{
//    public class GetLoadingUnitContentStepProvider : IStepProvider
//    {
//        private readonly IServiceProvider _services;

//        public GetLoadingUnitContentStepProvider(IServiceProvider serviceProvider)
//        {
//            _services = serviceProvider;
//        }

//        public ILoadingUnitStep CreateStep(ILoadingUnitMission mission, int stepId, IPosition? source = null, IPosition? destination = null, StepStatus status = StepStatus.New, string? descr = null, ILink? link = null, ICompoundLink? compoundLink = null)
//         => new GetLoadingUnitContentsStep
//            (
//                mission,
//                stepId,
//                _services,
//                status,
//                descr,
//                link,
//                compoundLink
//            );

//        public string StepType => nameof(GetLoadingUnitContentsStep);
//    }
//}