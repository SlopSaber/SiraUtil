using Zenject;

namespace SiraUtil.Installers
{
    internal class SiraSettingsInstaller : Installer
    {
        private readonly Config _config;

        public SiraSettingsInstaller(Config config)
        {
            _config = config;
        }

        public override void InstallBindings()
        {
            Container.BindInstance(_config.SongControl).AsSingle();
        }
    }
}
