namespace OnlyT.ViewModel
{
    // ReSharper disable CatchAllClause
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Forms;
    using System.Windows.Interop;
    using System.Windows.Media;
    using System.Windows.Threading;
    using EventArgs;
    using GalaSoft.MvvmLight;
    using GalaSoft.MvvmLight.Messaging;
    using GalaSoft.MvvmLight.Threading;
    using MaterialDesignThemes.Wpf;
    using Messages;
    using Models;
    using OnlyT.Services.JwLibrary;
    using OnlyT.Services.Snackbar;
    using Serilog;
    using Services.CommandLine;
    using Services.CountdownTimer;
    using Services.Monitors;
    using Services.Options;
    using Services.Timer;
    using Utils;
    using WebServer;
    using Windows;

    /// <inheritdoc />
    /// <summary>
    /// View model for the main page (which is a placeholder for the Operator or Settings page)
    /// </summary>
    /// <remarks>Needs refactoring to move _timerWindow and _countdownWindow into a "window service"</remarks>
    // ReSharper disable once ClassNeverInstantiated.Global
    public class MainViewModel : ViewModelBase
    {
        private readonly Dictionary<string, FrameworkElement> _pages = new Dictionary<string, FrameworkElement>();
        private readonly IOptionsService _optionsService;
        private readonly IMonitorsService _monitorsService;
        private readonly ICountdownTimerTriggerService _countdownTimerTriggerService;
        private readonly ITalkTimerService _timerService;
        private readonly ICommandLineService _commandLineService;
        private readonly IHttpServer _httpServer;
        private readonly (int dpiX, int dpiY) _systemDpi;
        private readonly ISnackbarService _snackbarService;
        private DispatcherTimer _heartbeatTimer;
        private bool _countdownDone;
        private TimerOutputWindow _timerWindow;
        private CountdownWindow _countdownWindow;
        private FrameworkElement _currentPage;
        
        public MainViewModel(
           IOptionsService optionsService,
           IMonitorsService monitorsService,
           ITalkTimerService timerService,
           ISnackbarService snackbarService,
           IHttpServer httpServer,
           ICommandLineService commandLineService,
           ICountdownTimerTriggerService countdownTimerTriggerService)
        {
            _commandLineService = commandLineService;

            if (commandLineService.NoGpu || ForceSoftwareRendering())
            {
                // disable hardware (GPU) rendering so that it's all done by the CPU...
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            }

            _snackbarService = snackbarService;
            _optionsService = optionsService;
            _monitorsService = monitorsService;
            _httpServer = httpServer;
            _timerService = timerService;
            _countdownTimerTriggerService = countdownTimerTriggerService;

            _httpServer.RequestForTimerDataEvent += OnRequestForTimerData;

            _systemDpi = WindowPlacement.GetDpiSettings();

            // subscriptions...
            Messenger.Default.Register<NavigateMessage>(this, OnNavigate);
            Messenger.Default.Register<TimerMonitorChangedMessage>(this, OnTimerMonitorChanged);
            Messenger.Default.Register<CountdownMonitorChangedMessage>(this, OnCountdownMonitorChanged);
            Messenger.Default.Register<AlwaysOnTopChangedMessage>(this, OnAlwaysOnTopChanged);
            Messenger.Default.Register<HttpServerChangedMessage>(this, OnHttpServerChanged);
            Messenger.Default.Register<StopCountDownMessage>(this, OnStopCountdown);

            InitHttpServer();

            // should really create a "page service" rather than create views in the main view model!
            _pages.Add(OperatorPageViewModel.PageName, new OperatorPage());

            Messenger.Default.Send(new NavigateMessage(null, OperatorPageViewModel.PageName, null));

#pragma warning disable 4014

            // (fire and forget)
            LaunchTimerWindowAsync();

#pragma warning restore 4014

            InitHeartbeatTimer();
        }

        public ISnackbarMessageQueue TheSnackbarMessageQueue => _snackbarService.TheSnackbarMessageQueue;

        public FrameworkElement CurrentPage
        {
            get => _currentPage;
            set
            {
                if (!ReferenceEquals(_currentPage, value))
                {
                    _currentPage = value;
                    RaisePropertyChanged();
                }
            }
        }

        public bool AlwaysOnTop
        {
            get
            {
                var result = _optionsService.Options.AlwaysOnTop ||
                             (_timerWindow != null && _timerWindow.IsVisible) ||
                             (_countdownWindow != null && _countdownWindow.IsVisible);

                return result;
            }
        }

        public string CurrentPageName { get; private set; }

        private bool CountDownActive => _countdownWindow != null;

        public void Closing(CancelEventArgs e)
        {
            e.Cancel = _timerService.IsRunning;
            if (!e.Cancel)
            {
                Messenger.Default.Send(new ShutDownMessage(CurrentPageName));
                CloseTimerWindow();
                CloseCountdownWindow();
            }
        }

        private void InitSettingsPage()
        {
            // we only init the settings page when first used.
            if (!_pages.ContainsKey(SettingsPageViewModel.PageName))
            {
                _pages.Add(SettingsPageViewModel.PageName, new SettingsPage(_commandLineService));
            }
        }

        private void OnHttpServerChanged(HttpServerChangedMessage msg)
        {
            _httpServer.Stop();
            InitHttpServer();
        }

        private void InitHttpServer()
        {
            if (_optionsService.Options.IsWebClockEnabled || _optionsService.Options.IsApiEnabled)
            {
                _httpServer.Start(_optionsService.Options.HttpServerPort);
            }
        }

        private void OnRequestForTimerData(object sender, TimerInfoEventArgs timerData)
        {
            // we received a web request for the timer clock info...
            var info = _timerService.GetClockRequestInfo();

            if (info == null || !info.IsRunning)
            {
                timerData.Mode = ClockServerMode.TimeOfDay;
            }
            else
            {
                timerData.Mode = ClockServerMode.Timer;

                timerData.TargetSecs = info.TargetSeconds;
                timerData.Mins = (int)info.ElapsedTime.TotalMinutes;
                timerData.Secs = info.ElapsedTime.Seconds;
                timerData.Millisecs = info.ElapsedTime.Milliseconds;
            }
        }

        /// <summary>
        /// Responds to change in the application's "Always on top" option.
        /// </summary>
        /// <param name="message">AlwaysOnTopChangedMessage message.</param>
        private void OnAlwaysOnTopChanged(AlwaysOnTopChangedMessage message)
        {
            RaisePropertyChanged(nameof(AlwaysOnTop));
        }

        /// <summary>
        /// Responds to a change in timer monitor.
        /// </summary>
        /// <param name="message">TimerMonitorChangedMessage message.</param>
        private void OnTimerMonitorChanged(TimerMonitorChangedMessage message)
        {
            try
            {
                if (_optionsService.IsTimerMonitorSpecified)
                {
                    RelocateTimerWindow();

                    if (CountDownActive)
                    {
                        // ensure countdown is topmost if running
                        _countdownWindow.Activate();
                    }
                }
                else
                {
                    HideTimerWindow();
                }

                RaisePropertyChanged(nameof(AlwaysOnTop));
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not change monitor");
            }
        }

        /// <summary>
        /// Responds to a change in countdown monitor.
        /// </summary>
        /// <param name="message">CountdownMonitorChangedMessage message.</param>
        private void OnCountdownMonitorChanged(CountdownMonitorChangedMessage message)
        {
            try
            {
                if (_optionsService.IsCountdownMonitorSpecified)
                {
                    RelocateCountdownWindow();
                }
                else
                {
                    HideCountdownWindow();
                }

                RaisePropertyChanged(nameof(AlwaysOnTop));
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not change monitor");
            }
        }

        private async Task LaunchTimerWindowAsync()
        {
            if (!IsInDesignMode && _optionsService.IsTimerMonitorSpecified)
            {
                // on launch we display the timer window after a short delay (for aesthetics only)
                await Task.Delay(1000).ConfigureAwait(true);
                OpenTimerWindow();
            }
        }

        private void InitHeartbeatTimer()
        {
            if (!IsInDesignMode)
            {
                _heartbeatTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };

                _heartbeatTimer.Tick += HeartbeatTimerTick;
                _heartbeatTimer.Start();
            }
        }

        private void HeartbeatTimerTick(object sender, EventArgs e)
        {
            _heartbeatTimer.Stop();
            try
            {
                if (_optionsService.IsCountdownMonitorSpecified)
                {
                    if (!CountDownActive &&
                        !_countdownDone &&
                        _countdownTimerTriggerService.IsInCountdownPeriod(out var secondsOffset))
                    {
                        StartCountdown(secondsOffset);
                    }
                }
                else
                {
                    // countdown not enabled...
                    if (CountDownActive)
                    {
                        StopCountdown();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Error during heartbeat");
            }
            finally
            {
                _heartbeatTimer.Start();
            }
        }

        private void OnStopCountdown(StopCountDownMessage message)
        {
            StopCountdown();
        }

        private void StopCountdown()
        {
            _countdownDone = true;

            Messenger.Default.Send(new CountdownWindowStatusChangedMessage { Showing = false });

            if (_optionsService.IsTimerMonitorSpecified)
            {
                _timerWindow?.Show();
            }

            Task.Delay(1000).ContinueWith(t =>
            {
                DispatcherHelper.CheckBeginInvokeOnUI(CloseCountdownWindow);
            });
        }

        /// <summary>
        /// Responds to the NavigateMessage and swaps out one page for another.
        /// </summary>
        /// <param name="message">NavigateMessage message.</param>
        private void OnNavigate(NavigateMessage message)
        {
            if (message.TargetPageName.Equals(SettingsPageViewModel.PageName))
            {
                // we only init the settings page when first used...
                InitSettingsPage();
            }

            CurrentPage = _pages[message.TargetPageName];
            CurrentPageName = message.TargetPageName;

            var page = (IPage)CurrentPage.DataContext;
            page.Activated(message.State);
        }

        /// <summary>
        /// If the timer window is open when we change the timer display then relocate it;
        /// otherwise open it
        /// </summary>
        private void RelocateTimerWindow()
        {
            if (_timerWindow != null)
            {
                RelocateWindow(_timerWindow, _monitorsService.GetMonitorItem(_optionsService.Options.TimerMonitorId));
            }
            else
            {
                OpenTimerWindow();
            }
        }

        /// <summary>
        /// If the countdown window is open when we change the timer display then relocate it;
        /// otherwise open it
        /// </summary>
        private void RelocateCountdownWindow()
        {
            if (_countdownWindow != null)
            {
                RelocateWindow(_countdownWindow, _monitorsService.GetMonitorItem(_optionsService.Options.CountdownMonitorId));
            }
        }

        private bool OpenCountdownWindow(int offsetSeconds)
        {
            if (!CountDownActive)
            {
                try
                {
                    var targetMonitor = _monitorsService.GetMonitorItem(_optionsService.Options.CountdownMonitorId);
                    if (targetMonitor != null)
                    {
                        _countdownWindow = new CountdownWindow();
                        _countdownWindow.TimeUpEvent += OnCountdownTimeUp;
                        ShowWindowFullScreenOnTop(_countdownWindow, targetMonitor);
                        _countdownWindow.Start(offsetSeconds);

                        Messenger.Default.Send(new CountdownWindowStatusChangedMessage { Showing = true });

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "Could not open countdown window");
                }
            }

            return false;
        }

        private void OnCountdownTimeUp(object sender, EventArgs e)
        {
            StopCountdown();
        }

        private void OpenTimerWindow()
        {
            try
            {
                var targetMonitor = _monitorsService.GetMonitorItem(_optionsService.Options.TimerMonitorId);
                if (targetMonitor != null)
                {
                    _timerWindow = new TimerOutputWindow(_optionsService);
                    ShowWindowFullScreenOnTop(_timerWindow, targetMonitor);
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not open timer window");
            }
        }

        private void LocateWindowAtOrigin(Window window, Screen monitor)
        {
            var area = monitor.WorkingArea;

            var left = (area.Left * 96) / _systemDpi.dpiX;
            var top = (area.Top * 96) / _systemDpi.dpiY;

            // these seemingly redundant sizing statements are required!
            window.Left = 0;
            window.Top = 0;
            window.Width = 0;
            window.Height = 0;

            window.Left = left;
            window.Top = top;
        }

        private void ShowWindowFullScreenOnTop(Window window, MonitorItem monitor)
        {
            LocateWindowAtOrigin(window, monitor.Monitor);

            window.Topmost = true;
            window.Show();

            RaisePropertyChanged(nameof(AlwaysOnTop));
        }

        private void HideTimerWindow()
        {
            _timerWindow?.Hide();
        }

        private void HideCountdownWindow()
        {
            _countdownWindow?.Hide();
        }

        private void CloseTimerWindow()
        {
            try
            {
                _timerWindow?.Close();
                _timerWindow = null;

                RaisePropertyChanged(nameof(AlwaysOnTop));
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not close timer window");
            }
        }

        private void CloseCountdownWindow()
        {
            try
            {
                if (_countdownWindow != null)
                {
                    _countdownWindow.TimeUpEvent -= OnCountdownTimeUp;
                }

                _countdownWindow?.Close();
                _countdownWindow = null;

                BringJwlToFront();

                RaisePropertyChanged(nameof(AlwaysOnTop));
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not close countdown window");
            }
        }

        private void BringJwlToFront()
        {
            if (_optionsService.Options.JwLibraryCompatibilityMode)
            {
                JwLibHelper.BringToFront();
                Thread.Sleep(100);
            }
        }

        /// <summary>
        /// Starts the countdown (pre-meeting) timer
        /// </summary>
        /// <param name="offsetSeconds">
        /// The offset in seconds (the timer already started offsetSeconds ago).
        /// </param>
        private void StartCountdown(int offsetSeconds)
        {
            if (!IsInDesignMode && _optionsService.IsCountdownMonitorSpecified)
            {
                Log.Logger.Information("Launching countdown timer");

                if (OpenCountdownWindow(offsetSeconds))
                {
                    Task.Delay(1000).ContinueWith(t =>
                    {
                        if (_optionsService.Options.TimerMonitorId == _optionsService.Options.CountdownMonitorId)
                        {
                            // timer monitor and countdown monitor are the same.

                            // hide the timer window after a short delay (so that it doesn't appear 
                            // as another top-level window during alt-TAB)...
                            DispatcherHelper.CheckBeginInvokeOnUI(HideTimerWindow);
                        }
                    });
                }
            }
        }

        private bool ForceSoftwareRendering()
        {
            // https://blogs.msdn.microsoft.com/jgoldb/2010/06/22/software-rendering-usage-in-wpf/
            // renderingTier values:
            // 0 => No graphics hardware acceleration available for the application on the device
            //      and DirectX version level is less than version 7.0
            // 1 => Partial graphics hardware acceleration available on the video card. This 
            //      corresponds to a DirectX version that is greater than or equal to 7.0 and 
            //      less than 9.0.
            // 2 => A rendering tier value of 2 means that most of the graphics features of WPF 
            //      should use hardware acceleration provided the necessary system resources have 
            //      not been exhausted. This corresponds to a DirectX version that is greater 
            //      than or equal to 9.0.
            int renderingTier = RenderCapability.Tier >> 16;
            return renderingTier == 0;
        }

        private void RelocateWindow(Window window, MonitorItem monitorItem)
        {
            if (monitorItem != null)
            {
                window.Hide();
                window.WindowState = WindowState.Normal;

                LocateWindowAtOrigin(window, monitorItem.Monitor);

                window.Topmost = true;
                window.WindowState = WindowState.Maximized;
                window.Show();
            }
        }
    }
}