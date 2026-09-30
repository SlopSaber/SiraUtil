using UnityEngine;
using Zenject;

namespace SiraUtil.Tools.SongControl
{
    internal class SongControlManager : ITickable
    {
        private readonly ISongControl _songControl;
        private readonly SongControlOptions _songControlOptions;
        private readonly GameScenesManager _gameScenesManager;

        public SongControlManager(ISongControl songControl, SongControlOptions songControlOptions, GameScenesManager gameScenesManager)
        {
            _songControl = songControl;
            _songControlOptions = songControlOptions;
            _gameScenesManager = gameScenesManager;
        }

        public void Tick()
        {
            // Gameplay ticks can start before scene loading finishes. A premature
            // quit publishes finish results even though the game rejects the scene pop.
            if (_gameScenesManager.isInTransition)
            {
                return;
            }

            if (Input.GetKeyDown(_songControlOptions.ExitKeyCode))
            {
                _songControl.Quit();
            }
            else if (Input.GetKeyDown(_songControlOptions.RestartKeyCode))
            {
                _songControl.Restart();
            }
            else if (Input.GetKeyDown(_songControlOptions.PauseToggleKeyCode))
            {
                if (_songControl.IsPaused)
                {
                    _songControl.Continue();
                }
                else
                {
                    _songControl.Pause();
                }
            }
        }
    }
}
