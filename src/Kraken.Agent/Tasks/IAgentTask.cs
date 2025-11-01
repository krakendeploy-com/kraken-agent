namespace Kraken.Agent.Tasks;

/// <summary>
///     Interface for handling agent command tasks.
/// </summary>
/// <typeparam name="TCommand">The type of command to handle</typeparam>
public interface IAgentCommandTask<TCommand>
{
    /// <summary>
    ///     Handles the specified command asynchronously.
    /// </summary>
    Task HandleAsync(TCommand command);
}