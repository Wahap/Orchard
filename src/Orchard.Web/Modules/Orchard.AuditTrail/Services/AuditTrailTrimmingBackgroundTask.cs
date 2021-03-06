﻿using System;
using System.Linq;
using System.Threading;
using Orchard.AuditTrail.Models;
using Orchard.ContentManagement;
using Orchard.Environment.Extensions;
using Orchard.Logging;
using Orchard.Services;
using Orchard.Settings;
using Orchard.Tasks;
using Orchard.Tasks.Locking.Services;

namespace Orchard.AuditTrail.Services {
    [OrchardFeature("Orchard.AuditTrail.Trimming")]
    public class AuditTrailTrimmingBackgroundTask : Component, IBackgroundTask {
        private static readonly object _sweepLock = new object();
        private readonly ISiteService _siteService;
        private readonly IClock _clock;
        private readonly IAuditTrailManager _auditTrailManager;
        private readonly IDistributedLockService _distributedLockService;

        public AuditTrailTrimmingBackgroundTask(
            ISiteService siteService,
            IClock clock,
            IAuditTrailManager auditTrailManager,
            IDistributedLockService distributedLockService) {

            _siteService = siteService;
            _clock = clock;
            _auditTrailManager = auditTrailManager;
            _distributedLockService = distributedLockService;
        }

        public AuditTrailTrimmingSettingsPart Settings {
            get { return _siteService.GetSiteSettings().As<AuditTrailTrimmingSettingsPart>(); }
        }

        public void Sweep() {
            if (Monitor.TryEnter(_sweepLock)) {
                try {
                    Logger.Debug("Beginning sweep.");

                    // Only allow this task to run on one farm node at a time.
                    IDistributedLock @lock;
                    if (_distributedLockService.TryAcquireLock(GetType().FullName, TimeSpan.FromHours(1), out @lock)) {
                        using (@lock) {

                            // We don't need to check the audit trail for events to remove every minute. Let's stick with twice a day.
                            if (!GetIsTimeToTrim())
                                return;

                            Logger.Debug("Starting audit trail trimming.");
                            var deletedRecords = _auditTrailManager.Trim(TimeSpan.FromDays(Settings.RetentionPeriod));
                            Logger.Debug("Audit trail trimming completed. {0} records were deleted.", deletedRecords.Count());
                            Settings.LastRunUtc = _clock.UtcNow;
                        }
                    }
                }
                catch (Exception ex) {
                    Logger.Error(ex, "Error during sweep.");
                }
                finally {
                    Monitor.Exit(_sweepLock);
                    Logger.Debug("Ending sweep.");
                }
            }
        }

        private bool GetIsTimeToTrim() {
            var lastRun = Settings.LastRunUtc ?? DateTime.MinValue;
            var now = _clock.UtcNow;
            var interval = TimeSpan.FromHours(Settings.MinimumRunInterval);
            return now - lastRun >= interval;
        }
    }
}