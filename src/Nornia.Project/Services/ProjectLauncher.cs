using Nornia.Core.Interfaces;

namespace Nornia.Project.Services;

public interface IProjectLauncher
{
    Task OpenAsync(string projectPath, string editorCommand = "code", CancellationToken cancellationToken = default);
}

public sealed class ProjectLauncher(IProcessRunner processRunner) : IProjectLauncher
{
    public async Task OpenAsync(string projectPath, string editorCommand = "code", CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(projectPath);
        // The external-editor command is also used by the file preview. In that flow the
        // argument is a file (for example docs/development.md), while project launch still
        // passes a directory. Both are valid targets for editors such as VS Code.
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Path '{fullPath}' does not exist.", fullPath);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(editorCommand);
        // Shell-execute so PATH batch shims ("code" → code.cmd) resolve like they do in a
        // terminal; a redirected CreateProcess cannot start those (Nornia's ProcessRunner is
        // redirect-oriented, so launching editors goes through the shell path).
        var result = await processRunner.RunShellAsync(editorCommand, [fullPath], cancellationToken: cancellationToken);
        if (!result.IsSuccess)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();
            throw new InvalidOperationException($"Unable to open project with '{editorCommand}': {detail}");
        }
    }
}
