using HMUI;
using System;
using Zenject;

namespace SiraUtil.Submissions
{
    internal abstract class SubmissionDisplayer : IInitializable, ITickable, IDisposable
    {
        private readonly ViewController _resultsViewController;
        private readonly FlowCoordinator _targetFlowCoordinator;
        private bool _waitingForText;
        private bool _disposed;
        private int _dataRevision;

        [Inject]
        private readonly SubmissionDataContainer _submissionDataContainer = null!;

        [Inject]
        private readonly SiraSubmissionViewController _siraSubmissionViewController = null!;

        public SubmissionDisplayer(FlowCoordinator targetFlowCoordinator, ViewController resultsViewController)
        {
            _resultsViewController = resultsViewController;
            _targetFlowCoordinator = targetFlowCoordinator;
        }

        public void Initialize()
        {
            _resultsViewController.didActivateEvent += ResultsViewController_didActivateEvent;
            _resultsViewController.didDeactivateEvent += ResultsViewController_didDeactivateEvent;
        }

        private void ResultsViewController_didActivateEvent(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (_submissionDataContainer.Disabled)
            {
                _dataRevision = _submissionDataContainer.Revision;
                _waitingForText = true;
                _targetFlowCoordinator.SetBottomScreenViewController(_siraSubmissionViewController, ViewController.AnimationType.In);
                _siraSubmissionViewController.SetText("");
                _siraSubmissionViewController.Enabled(false);
                Tick();
            }
        }

        public void Tick()
        {
            if (!_waitingForText || _disposed)
            {
                return;
            }

            if (!_submissionDataContainer.Disabled || _dataRevision != _submissionDataContainer.Revision || _resultsViewController == null || _siraSubmissionViewController == null)
            {
                _waitingForText = false;
                return;
            }

            if (!_siraSubmissionViewController.IsReady || !_siraSubmissionViewController.isInViewControllerHierarchy)
            {
                return;
            }

            if (_submissionDataContainer.TryRead(out string text))
            {
                _waitingForText = false;
                _siraSubmissionViewController.SetText(text);
                _siraSubmissionViewController.Enabled(true);
            }
        }

        private void ResultsViewController_didDeactivateEvent(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            _waitingForText = false;
            _submissionDataContainer.Disabled = false;
            _siraSubmissionViewController.Enabled(false);
            if (_siraSubmissionViewController.isInViewControllerHierarchy)
            {
                _targetFlowCoordinator.SetBottomScreenViewController(null, ViewController.AnimationType.Out);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _waitingForText = false;
            _resultsViewController.didDeactivateEvent -= ResultsViewController_didDeactivateEvent;
            _resultsViewController.didActivateEvent -= ResultsViewController_didActivateEvent;
        }
    }
}
