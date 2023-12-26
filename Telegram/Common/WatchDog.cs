﻿using Microsoft.AppCenter;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Converters;
using Telegram.Native;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.System;
using Windows.System.Profile;
using Windows.UI.Xaml;
using File = System.IO.File;

namespace Telegram
{
    /*
     * How does this work?
     * 
     * We use a fork of the AppCenter SDK to get more accurate error reports.
     * The end goal is to distinguish handled and unhandled exceptions,
     * as well to get some insights about unmanaged crashes that
     * would be otherwise invisible to us.
     * 
     * When the framework reports a managed unhandled exception via UnhandledException,
     * AppCenter SDK will raise CreatingErrorReport, providing a report id to associate
     * the exception data with the additional logs that should be sent alongside the report.
     * When this happens, crash.log is updated using the report id.
     * 
     * If the process terminates smoothly, we delete crash.log.
     * This happens in Application.Suspending.
     * 
     * On the subsequent app launch, we check if crash.log exist and contains a report id.
     * If this is the case, we will mark the report as a crash by returning true in 
     * ShouldProcessErrorReport.
     * 
     * We're also monitoring unmanaged exceptions by registering
     * SetUnhandledExceptionFilter on DLL_THREAD_ATTACH from Telegram.Native/dllmain.cpp.
     * Whenever an unmanaged exception is thrown, we're going to wrap it
     * into an UnmanagedException object, and pass it to Crashes.TrackCrash.
     * 
     * Symbolification of unmanaged exceptions is done manually by using CDB.exe as follows:
     * cdb -lines -z "{path to dll}" -y "{path to symbols}"
     * 
     * 0.000> u 0x{base + address}; q
     * 
     * base is 0x180000000 for x64 and 0x10000000 for x86
     * 
     */

    public class WatchDog
    {
        private static readonly bool _disabled = Constants.DEBUG;

        private static readonly string _crashLog;
        private static readonly string _reports;

        private static string _lastSessionErrorReportId;
        private static bool _lastSessionTerminatedUnexpectedly;

        static WatchDog()
        {
            _crashLog = Path.Combine(ApplicationData.Current.LocalFolder.Path, "crash.log");
            _reports = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Reports");
        }

        public static bool HasCrashedInLastSession { get; private set; }

        public static void Initialize()
        {
            NativeUtils.SetFatalErrorCallback(FatalErrorCallback);

            Client.SetLogMessageCallback(Constants.DEBUG ? 5 : 0, FatalErrorCallback);

            BootStrapper.Current.UnhandledException += (s, args) =>
            {
                Logger.Error("UnhandledException 0x" + args.Exception.HResult.ToString("X4"));

                if (args.Exception is LayoutCycleException)
                {
                    Analytics.TrackEvent("LayoutCycleException", new Dictionary<string, string>
                    {
                        { "UseLayoutRounding", SettingsService.Current.Diagnostics.UseLayoutRounding.ToString() },
                        { "TabsAlignedLeft", SettingsService.Current.IsLeftTabsEnabled.ToString() },
                    });

                    if (ApiInfo.IsPackagedRelease)
                    {
                        SettingsService.Current.Diagnostics.LastCrashWasLayoutCycle = true;
                        SettingsService.Current.Diagnostics.UseLayoutRounding = false;
                    }
                }

                args.Handled = args.Exception is not LayoutCycleException;
            };

            if (_disabled)
            {
                return;
            }

            Read();

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Crashes.TrackCrash(args.Exception);
                args.SetObserved();
            };

            Crashes.CreatingErrorReport += (s, args) =>
            {
                Track(args.ReportId, args.Exception);
            };

            Crashes.SentErrorReport += (s, args) =>
            {
                if (File.Exists(GetErrorReportPath(args.Report.Id)))
                {
                    File.Delete(GetErrorReportPath(args.Report.Id));
                }
            };

            Crashes.ShouldProcessErrorReport = report =>
            {
                return report.Id == _lastSessionErrorReportId;
            };

            Crashes.GetErrorAttachments = report =>
            {
                var path = GetErrorReportPath(report.Id);
                if (path.Length > 0 && File.Exists(path))
                {
                    var data = File.ReadAllText(path);
                    return new[] { ErrorAttachmentLog.AttachmentWithText(data, "crash.txt") };
                }

                return Array.Empty<ErrorAttachmentLog>();
            };

            AppCenter.Start(Constants.AppCenterId, typeof(Analytics), typeof(Crashes));
            Analytics.TrackEvent("Windows",
                new Dictionary<string, string>
                {
                    { "DeviceFamily", AnalyticsInfo.VersionInfo.DeviceFamily },
                    { "Architecture", Package.Current.Id.Architecture.ToString() }
                });
        }

        public static void TrackEvent(string name)
        {
            if (_disabled)
            {
                return;
            }

            Analytics.TrackEvent(name);
        }

        private static void Read()
        {
            if (File.Exists(_crashLog))
            {
                _lastSessionTerminatedUnexpectedly = true;

                var data = File.ReadAllText(_crashLog);

                if (Guid.TryParse(data, out Guid guid))
                {
                    _lastSessionErrorReportId = guid.ToString();
                }

                File.Delete(_crashLog);
            }
        }

        public static void FatalErrorCallback(string message)
        {
            Crashes.TrackCrash(new UnmanagedException(message));
        }

        private static void FatalErrorCallback(int verbosityLevel, string message)
        {
            if (verbosityLevel != 0)
            {
#if DEBUG
                if (verbosityLevel == 1 && message.Contains("File remote location was changed from"))
                {
                    var source = Path.Combine(ApplicationData.Current.LocalFolder.Path, "tdlib_log.txt");
                    var destination = Path.ChangeExtension(source, ".backup");

                    File.Copy(source, destination, true);
                }
#endif

                return;
            }

            var exception = TdException.FromMessage(message);
            if (exception.IsUnhandled)
            {
                Crashes.TrackCrash(exception);
            }
        }

        public static void Launch(ApplicationExecutionState previousExecutionState)
        {
            // NotRunning: An app could be in this state because it hasn't been launched
            // since the last time the user rebooted or logged in. It can also be in this
            // state if it was running but then crashed, or because the user closed it earlier.

            HasCrashedInLastSession =
                _lastSessionErrorReportId != null
                && previousExecutionState == ApplicationExecutionState.NotRunning;
        }

        private static void Track(string reportId, Exception exception)
        {
            var version = VersionLabel.GetVersion();

            var next = DateTime.Now.ToTimestamp();
            var prev = SettingsService.Current.Diagnostics.LastUpdateTime;

            var count = SettingsService.Current.Diagnostics.UpdateCount;

            var memoryUsage = FileSizeConverter.Convert((long)MemoryManager.AppMemoryUsage);
            var memoryUsageLimit = FileSizeConverter.Convert((long)MemoryManager.AppMemoryUsageLimit);

            var info =
                $"Current version: {version}\n" +
                $"Memory usage: {memoryUsage}\n" +
                $"Memory usage level: {MemoryManager.AppMemoryUsageLevel}\n" +
                $"Memory usage limit: {memoryUsageLimit}\n" +
                $"Time since last update: {next - prev}s\n" +
                $"Update count: {count}\n" + 
                $"Layout rounding: {SettingsService.Current.Diagnostics.UseLayoutRounding}\n" +
                $"Tabs on the left: {SettingsService.Current.IsLeftTabsEnabled}\n" +
                $"HRESULT: 0x{exception.HResult:X4}\n";

            if (WindowContext.Current != null)
            {
                var scaling = (WindowContext.Current.RasterizationScale * 100).ToString("N0");
                var size = WindowContext.Current.Size;

                info += $"Screen scaling: {scaling}%\n" +
                    $"Window size: {size.Width}x{size.Height}\n";
            }

            info += "\n";

            var dump = Logger.Dump();
            var payload = info + dump;

            File.WriteAllText(_crashLog, reportId);
            File.WriteAllText(GetErrorReportPath(reportId), payload);
        }

        private static string GetErrorReportPath(string reportId)
        {
            Directory.CreateDirectory(_reports);
            return Path.Combine(ApplicationData.Current.LocalFolder.Path, _reports, reportId + ".appcenter");
        }

        public static void Suspend()
        {
            if (File.Exists(_crashLog))
            {
                File.Delete(_crashLog);
            }
        }
    }

    public class UnmanagedException : Exception
    {
        public UnmanagedException(string message)
            : base(message)
        {
        }
    }
}
