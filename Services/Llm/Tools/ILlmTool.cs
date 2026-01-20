using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Llm.Tools
{
    /// <summary>
    /// Interface for tools that the LLM can invoke.
    /// </summary>
    public interface ILlmTool
    {
        /// <summary>
        /// Unique name of the tool (used in LLM prompts and parsing).
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Human-readable description of what the tool does.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// JSON schema describing the tool's parameters for the LLM.
        /// </summary>
        string ParameterSchema { get; }

        /// <summary>
        /// Execute the tool with the given parameters.
        /// </summary>
        /// <param name="parameters">JSON string containing the parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result string to be included in the conversation.</returns>
        Task<string> ExecuteAsync(string parameters, CancellationToken ct = default);
    }
}
