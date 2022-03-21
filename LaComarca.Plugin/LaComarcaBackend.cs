using Automha.L2.Common.Entities.Classes.Results;
using Automha.L2.Plugin.Abstraction;
using Automha.Warehouse.Abstractions;
using Automha.Warehouse.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LaComarca.Plugin
{
   public class LaComarcaBackend : IBackendFunctions
    {

        private readonly ILogger<LaComarcaBackend> _log;
        private readonly IMissionManager _mm;
        private readonly IPlant _plant;
        private readonly IServiceProvider _services;
        private readonly IEventRepository _eventRepository;

        public LaComarcaBackend
            (
                ILogger<LaComarcaBackend> logger,
                IServiceProvider services,
                IEventRepository eventRepository,
                IMissionManager missionManager,
                IPlant plant
            )
        {
            _log = logger;
            _mm = missionManager;
            _plant = plant;
            _services = services;
            _eventRepository = eventRepository;
        }

        public async Task<ResultData> LockComarca(int partitionId, string reason, ILoggedUser user, CancellationToken token = default)
        {
            await Task.Yield();
            _log.LogWarning($"User: {user.DisplayName} wants to Lock partitionId:{partitionId} for reason:{reason}");

            try
            {
                var partition = _plant.Partitions.Single(p => p.Id == partitionId);

                if (partition.GetItemType() == ItemType.PeakMover)
                {
                    var partitions = partition.LocatedIn.LocatedIn.SubItems.SelectMany(p => p.Partitions);
                    foreach (var p in partitions)
                    {
                        _plant.LockUnlockPartition(partition.Id, true, true, reason);
                        VerifyGroupToLockUnLock(partitionId, true);
                    }
                }
                else
                {
                    _plant.LockUnlockPartition(partition.Id, true, true, reason);
                    VerifyGroupToLockUnLock(partitionId, true);
                }

                return ResultData.Ok();
            }
            catch (Exception ex)
            {
                _log.LogError(ex.ToString());
                return ResultData.FromException(ex);
            }
        }

        private void LockUnLockGroup(int groupId, bool lockFlag)
        {
            var group = _plant.LinkGroups.Single(x => x.Id == groupId);
            if (group is null)
            {
                _log.LogError($"Cannot find LinkGroup with Id {groupId}");
                return;
            }

            foreach (var link in group.Links)
                _plant.ChangeLinkEnable(link.Id, !lockFlag);
        }

        private void VerifyGroupToLockUnLock(int partitionId, bool lockFlag)
        {
                switch (partitionId)
                {
                    case 20410: //INGRESSO 2D10 (STANDARD)
                        LockUnLockGroup(1, !lockFlag);
                        LockUnLockGroup(2, lockFlag);
                        break;
                    case 20510: //USCITA 2E10 (STANDARD)
                    LockUnLockGroup(4, !lockFlag);
                        LockUnLockGroup(3, lockFlag);
                        break;
                }
        }
    }
}
