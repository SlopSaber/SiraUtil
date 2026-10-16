namespace SiraUtil.Submissions
{
    internal class SiraLevelCompletionResults : LevelCompletionResults
    {
        public bool ShouldSubmitScores { get; }

        public SiraLevelCompletionResults(LevelCompletionResults lcr, bool scoreSubmission) : base(lcr.gameplayModifiers,
#if BEAT_SABER_1453
            lcr.score,
#else
            lcr.modifiedScore, lcr.multipliedScore,
#endif
            lcr.rank, lcr.fullCombo, lcr.leftSaberMovementDistance, lcr.rightSaberMovementDistance, lcr.leftHandMovementDistance,
            lcr.rightHandMovementDistance, lcr.levelEndStateType, lcr.levelEndAction, lcr.energy, lcr.goodCutsCount,
            lcr.badCutsCount,
#if BEAT_SABER_1453
            lcr.missedCutsCount,
#else
            lcr.missedCount,
#endif
            lcr.notGoodCount, lcr.okCount, lcr.maxCutScore, lcr.totalCutScore, lcr.goodCutsCountForNotesWithFullScoreScoringType, lcr.averageCenterDistanceCutScoreForNotesWithFullScoreScoringType, lcr.averageCutScoreForNotesWithFullScoreScoringType,
            lcr.maxCombo, lcr.endSongTime, lcr.invalidated
#if BEAT_SABER_1453
            , lcr.startSongTime
#endif
            )
        {
            ShouldSubmitScores = scoreSubmission;
        }
    }
}
