using IPA.Loader;
using System;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SiraUtil.Submissions
{
    internal sealed class SubmissionDataContainer
    {
        private const string Heading = "<size=115%><color=#f03030>Score Submission Disabled By</color></size>\n";
        private string _data = "";
        private PropertyInfo? _ssssdi;
        private Task<string>? _preparation;
        private TicketText[]? _pending;
        public bool Disabled { get; set; }
        internal int Revision { get; private set; }

        internal void Set(bool disabled, Ticket[] tickets)
        {
            _data = "";
            Disabled = disabled;
            Revision++;
            TicketText[] snapshot = new TicketText[tickets.Length];
            for (int index = 0; index < tickets.Length; index++)
            {
                Ticket ticket = tickets[index];
                snapshot[index] = new TicketText(ticket.Source, ticket.Reasons());
            }

            _pending = snapshot;
            if (_preparation is null || _preparation.IsCompleted)
            {
                _ = _preparation?.Exception;
                StartPreparation();
            }
        }

        internal bool TryRead(out string text)
        {
            text = "";
            if (_preparation is not null)
            {
                if (!_preparation.IsCompleted)
                {
                    return false;
                }

                if (_pending is not null)
                {
                    _ = _preparation.Exception;
                    StartPreparation();
                    return false;
                }

                _data = _preparation.GetAwaiter().GetResult();
                _preparation = null;
            }

            text = _data;
            return true;
        }

        private void StartPreparation()
        {
            TicketText[] snapshot = _pending!;
            _pending = null;
            if (snapshot.Length == 0)
            {
                _preparation = Task.FromResult(Heading);
                return;
            }

            _preparation = Task.Factory.StartNew(
                static state => PrepareText((TicketText[])state!),
                snapshot,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        private static string PrepareText(TicketText[] tickets)
        {
            StringBuilder text = new(Heading);
            foreach (TicketText ticket in tickets)
            {
                text.Append(ticket.Source).Append('\n');
                foreach (string reason in ticket.Reasons)
                {
                    text.Append("<size=80%><color=#999999>").Append(reason).Append("</color></size>\n");
                }
            }

            return text.ToString();
        }

        private sealed class TicketText
        {
            internal string Source { get; }
            internal string[] Reasons { get; }

            internal TicketText(string source, string[] reasons)
            {
                Source = source;
                Reasons = reasons;
            }
        }

        internal void SSS(bool value)
        {
            if (_ssssdi is null)
            {
                PluginMetadata? scoreSaber = PluginManager.GetPluginFromId("ScoreSaber");
                if (scoreSaber is not null && scoreSaber.PluginType is not null)
                {
                    Type? type = scoreSaber.Assembly.GetType(scoreSaber.PluginType.FullName);
                    if (type != null)
                    {
                        _ssssdi = type.GetProperty("ScoreSubmission");
                    }
                }
            }

            _ssssdi?.SetValue(null, value);
        }
    }
}
