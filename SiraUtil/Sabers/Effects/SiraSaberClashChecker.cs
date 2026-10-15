using System;
using System.Collections.Generic;
using UnityEngine;
using Zenject;

namespace SiraUtil.Sabers.Effects
{
    internal interface ISiraClashChecker
    {
        event Action<Saber, Saber>? NewSabersClashed;

        bool ExtraSabersDetected { get; }

        bool AreSabersClashing(ref bool sabersAreClashing, ref Vector3 localClashingPoint, ref int prevGetFrameNum, out Vector3 clashingPoint);
    }

    internal class SiraSaberClashChecker : SaberClashChecker, ISiraClashChecker, IDisposable
    {
        private Saber? _lastSaberA;
        private Saber? _lastSaberB;
        private readonly List<Saber> _sabers = [];
        private ClashPairBatch? _pairBatch;
        private bool _disposed;
        public event Action<Saber, Saber>? NewSabersClashed;

        protected readonly DiContainer _container;
        protected readonly SaberManager _saberManager;
        protected readonly SiraSaberFactory _siraSaberFactory;

        public bool ExtraSabersDetected { get; private set; }

        public SiraSaberClashChecker(DiContainer container, SaberManager saberManager, SiraSaberFactory siraSaberFactory)
        {
            _container = container;
            _saberManager = saberManager;
            _siraSaberFactory = siraSaberFactory;
            _sabers.Add(_saberManager.leftSaber);
            _sabers.Add(_saberManager.rightSaber);
            _siraSaberFactory.SaberCreated += SiraSaberFactory_SaberCreated;
        }

        private void SiraSaberFactory_SaberCreated(SiraSaber siraSaber)
        {
            Saber saber = siraSaber.Saber;

            if (_sabers.Contains(saber))
            {
                return;
            }

            ExtraSabersDetected = true;

            _sabers.Add(saber);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _siraSaberFactory.SaberCreated -= SiraSaberFactory_SaberCreated;
            _pairBatch?.Dispose();
        }

        private ClashPairBatch? PreparePairBatch()
        {
            _pairBatch?.Dispose();
            return _disposed || _pairBatch is { IsCompleted: false } ? null : _pairBatch = ClashPairBatch.Start(_sabers);
        }

        public bool AreSabersClashing(ref bool sabersAreClashing, ref Vector3 localClashingPoint, ref int prevGetFrameNum, out Vector3 clashingPoint)
        {
            if (_leftSaber.movementDataForLogic.lastAddedData.time < 0.1f)
            {
                clashingPoint = localClashingPoint;
                return false;
            }
            if (prevGetFrameNum == Time.frameCount)
            {
                clashingPoint = localClashingPoint;
                return sabersAreClashing;
            }
            prevGetFrameNum = Time.frameCount;
            ClashPairBatch? pairBatch = PreparePairBatch();
            for (int i = 0; i < _sabers.Count; i++)
            {
                for (int h = 0; h < _sabers.Count; h++)
                {
                    if (i > h)
                    {
                        Saber saberA = _sabers[i];
                        Saber saberB = _sabers[h];
                        if (saberA == saberB || saberA == null || saberB == null)
                        {
                            break;
                        }
                        Vector3 saberBladeTopPos = saberA.saberBladeTopPos;
                        Vector3 saberBladeTopPos2 = saberB.saberBladeTopPos;
                        Vector3 saberBladeBottomPos = saberA.saberBladeBottomPos;
                        Vector3 saberBladeBottomPos2 = saberB.saberBladeBottomPos;

                        if (pairBatch == null || !pairBatch.TryTake(i, h, saberA, saberB, saberBladeBottomPos, saberBladeTopPos,
                            saberBladeBottomPos2, saberBladeTopPos2, out float distance, out Vector3 clashingPoint2))
                        {
                            distance = SegmentToSegmentDist(saberBladeBottomPos, saberBladeTopPos, saberBladeBottomPos2, saberBladeTopPos2, out clashingPoint2);
                        }
                        if (distance < 0.08f && saberA.isActiveAndEnabled && saberB.isActiveAndEnabled)
                        {
                            if (_lastSaberA == null && _lastSaberB == null)
                            {
                                _lastSaberA = saberA;
                                _lastSaberB = saberB;
                                NewSabersClashed?.Invoke(_lastSaberA, _lastSaberB);
                            }
                            localClashingPoint = clashingPoint2;
                            clashingPoint = localClashingPoint;
                            sabersAreClashing = true;
                            return sabersAreClashing;
                        }
                        else
                        {
                            _lastSaberA = null;
                            _lastSaberB = null;
                            sabersAreClashing = false;
                        }
                    }
                }
            }
            clashingPoint = localClashingPoint;
            return sabersAreClashing;
        }
    }
}
