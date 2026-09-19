using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DiscordRPC;
using DiscordRPC.Logging;

namespace LarpLand.Core
{
    public class DiscordManager : IDisposable
    {
        private DiscordRpcClient? _client;
        private bool _isInitialized;
        private string _currentState = "menu";
        private string _appId = Endpoints.DiscordAppId;

        public string LauncherVersion { get; set; } = "";
        public string ModpackVersion { get; set; } = "";

        public bool Available => IsAppId(_appId);

        private static bool IsAppId(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length >= 17 && value.Length <= 20 && value.All(char.IsDigit);

        // WHY: владелец сборки заводит приложение Discord сам, поэтому идентификатор
        // WHY: читается из репозитория версий - новый exe ради одной строки не нужен
        public async Task AdoptRemoteAppIdAsync(HttpClient http)
        {
            if (Available) return;

            try
            {
                string fetched = (await http.GetStringAsync(Endpoints.DiscordAppIdUrl + "?t=" + DateTime.Now.Ticks)).Trim();
                if (!IsAppId(fetched))
                {
                    LauncherLog.Write("[DISCORD] Удалённый идентификатор приложения не похож на настоящий, статус выключен");
                    return;
                }

                _appId = fetched;
                LauncherLog.Write("[DISCORD] Идентификатор приложения получен из репозитория версий");
                Initialize();
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                LauncherLog.Write($"[DISCORD] Идентификатор приложения не скачан: {error.Message}");
            }
        }

        public void Initialize()
        {
            if (_isInitialized) return;

            if (!Available)
            {
                LauncherLog.Write("[DISCORD] Идентификатор приложения не задан, статус в Discord выключен");
                return;
            }

            try
            {
                _client = new DiscordRpcClient(_appId);
                _client.Logger = new ConsoleLogger { Level = LogLevel.Warning };
                _client.OnConnectionFailed += (sender, e) =>
                    LauncherLog.Write($"[DISCORD] Подключение не удалось: труба {e.FailedPipe}");
                _client.Initialize();

                _isInitialized = true;
                LauncherLog.Write("[DISCORD] Статус подключён");
                SetMenuState();
            }
            catch (Exception error)
            {
                LauncherLog.Write($"[DISCORD] Статус не запустился: {error.Message}");
                _client = null;
                _isInitialized = false;
            }
        }

        public void SetMenuState()
        {
            _currentState = "menu";

            if (!Available) return;
            if (_client == null) { Initialize(); return; }
            if (!_client.IsInitialized) return;

            try
            {
                _client.SetPresence(new RichPresence
                {
                    Details = Lang.F("В лаунчере | v{0}", LauncherVersion),
                    State = Lang.F("Сборка {0}", ModpackVersion),
                    Assets = new Assets
                    {
                        LargeImageKey = "rpc_icon",
                        LargeImageText = "LarpLand"
                    },
                    Buttons = new[]
                    {
                        new Button { Label = "Скачать лаунчер", Url = Endpoints.LauncherExeUrl },
                        new Button { Label = "GitHub", Url = Endpoints.ProjectUrl }
                    }
                });
            }
            catch (Exception error)
            {
                LauncherLog.Write($"[DISCORD] Статус не обновлён: {error.Message}");
            }
        }

        // WHY: пока идёт игра, статус занимает мод внутри Minecraft, поэтому лаунчер
        // WHY: освобождает канал целиком, а не просто меняет текст
        public void ReleaseForGame()
        {
            _currentState = "playing";

            if (_client != null)
            {
                try { _client.ClearPresence(); } catch (Exception error) { LauncherLog.Write($"[DISCORD] Очистка статуса не прошла: {error.Message}"); }
                _client.Dispose();
                _client = null;
            }

            _isInitialized = false;
            LauncherLog.Write("[DISCORD] Статус отдан игре");
        }

        public void Dispose()
        {
            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }

            _isInitialized = false;
        }
    }
}
