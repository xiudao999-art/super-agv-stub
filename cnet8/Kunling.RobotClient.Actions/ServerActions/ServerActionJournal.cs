using System.Collections.Concurrent;

namespace Kunling.RobotClient.Actions.ServerActions;

public sealed class ServerActionJournal
{
    private readonly ConcurrentDictionary<string, ActionEvent> _latest = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string actionInstanceId, out ActionEvent? actionEvent) => _latest.TryGetValue(actionInstanceId, out actionEvent);

    public ActionEvent Save(ActionEvent actionEvent)
    {
        _latest[actionEvent.ActionInstanceId] = actionEvent;
        return actionEvent;
    }
}
