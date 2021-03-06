using System;
using GBCLV3.Models;
using GBCLV3.Models.Installation;
using GBCLV3.Services;
using GBCLV3.Services.Authentication;
using GBCLV3.Services.Launch;
using GBCLV3.Utils;
using GBCLV3.ViewModels.Pages;
using Stylet;
using StyletIoC;
using System.Windows;
using GBCLV3.ViewModels.Tabs;

namespace GBCLV3.ViewModels.Windows
{
    public class MainViewModel : Conductor<IScreen>.Collection.OneActive
    {
        #region Private Members

        // IoC
        private readonly Config _config;
        private readonly VersionService _versionService;

        private readonly Screen[] _pages;
        private readonly IWindowManager _windowManager;

        private readonly string[] _kaomojis =
        {
            " (⁄ ⁄•⁄ω⁄•⁄ ⁄)", "(⇀‸↼‶)", "(๑˘•◡•˘๑)", "_( '-' _)⌒)_", "(●—●)", "~( ´•︵•` )~", "(´；ω；`)", "(｡･ω･｡)",
            "( *・ω・)✄╰ひ╯", "(╯>д<)╯┻━┻", "_(-ω-`_)⌒)_", "ᕦ(･ㅂ･)ᕤ", "(◞‸◟ )", "(ㅎ‸ㅎ)", "(= ᵒᴥᵒ =)", "_(¦3」∠)_ ",
            "(๑乛◡乛๑)", "( ,,ÒωÓ,, )", "ε=ε=(ノ≧∇≦)ノ", "(･∀･)", "Σ( ￣□￣||)", "(。-`ω´-)", "(´• ᗜ •`)", "(๑╹∀╹๑)",
            "(´• ᵕ •`)*✲", "┑(￣Д ￣)┍", "(≖＿≖)✧ ", "(｡•ˇ‸ˇ•｡)", "\\(•ㅂ•)/", "(´･ᆺ･`)", "ԅ(¯﹃¯ԅ)", "୧(๑•∀•๑)૭",
            "ʕ•ﻌ•ʔ", "ヾ(*´∀ ˋ*)ﾉ ", "ヽ(●´∀`●)ﾉ ", "d(`･∀･)b ",
        };

        private readonly Random _random = new Random();

        #endregion

        #region Constructor

        [Inject]
        public MainViewModel(
            ConfigService configService,
            VersionService versionService,
            ThemeService themeService,

            LaunchViewModel launchVM,
            SettingsRootViewModel settingsVM,
            VersionsRootViewModel versionsVM,
            AuxiliariesRootViewModel auxVM,
            AboutViewModel aboutVM,

            IWindowManager windowManager)
        {
            _windowManager = windowManager;
            _config = configService.Entries;
            _versionService = versionService;

            _pages = new Screen[]
            {
                launchVM, settingsVM, versionsVM, auxVM, aboutVM,
            };

            // Start up with LaunchView
            ActivePageIndex = 0;

            // Bind background image service
            ThemeService = themeService;

            //Set Title
            this.DisplayName = "GBCL v" + AssemblyUtil.Version;

            // You don't want to switch to other pages while launching the game
            CanChangePage = true;
            launchVM.LaunchProcessStarted += () => CanChangePage = false;
            launchVM.LaunchProcessEnded += () => CanChangePage = true;
        }

        #endregion

        #region Bindings

        public ThemeService ThemeService { get; }

        public string Rambling => _kaomojis[_random.Next(_kaomojis.Length)];

        public int ActivePageIndex { get; set; }

        public bool CanChangePage { get; private set; }

        public void ChangeActivePage()
        {
            this.ActivateItem(_pages[ActivePageIndex]);
        }

        #endregion

        #region Override Methods

        protected override void OnInitialActivate()
        {
            // If no game version exists, navigate to version install view on user's discretion
            if (!_versionService.LoadAll())
            {
                if (_windowManager.ShowMessageBox("${WhetherInstallNewVersion}", "${NoVersionFound}",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    ActivePageIndex = 2;
                    (_pages[2] as VersionsRootViewModel).OnNavigateInstallView(null, InstallType.Version);
                }
            }
        }

        #endregion
    }
}
