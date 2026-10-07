using Alexander.Contracts;
using Alexander.Services.Contracts;
using Alexander.Shared;

namespace Alexander.Services.AI;

// The AI service's command layer. From the shell layer its commands are
// "/ai ask", "/ai set", ...; after "/ai" transfers control here they're
// just "ask", "set", ... and plain text is sent as a prompt (see
// HandleTextAsync). Registered by AddAlexanderAi.
public sealed class AiCommandLayer(
	IAiClient aiClient,
	IAiClientStore clientStore,
	ICurrentFolder currentFolder,
	IStateStore stateStore,
	IOutputWriter output) : IServiceCommandLayer {

	private delegate Task<DtResult> Handler(
		AiCommandLayer self,
		ParsedCommand command,
		CancellationToken cancellationToken);

	private sealed record Entry(CommandDefinition Definition, Handler Run);

	// Each command's definition sits next to its handler, so the two
	// can't drift apart. Text is relative to the layer - the shell layer
	// adds the "/ai" prefix.
	private static readonly Entry[] Entries = [
		new(
			new CommandDefinition("ask", "Sends a prompt to a configured AI client and prints the answer.", [
				new("--prompt", "The question or instruction; quote values containing spaces."),
				new("--id", "Saved AI client to use; defaults to /ai use's choice."),
				new("--file", "Optional file to include as context."),
				new("--output", "Optional path to also save the answer to.")
			]),
			(self, command, ct) => self.AskAsync(command, ct)),

		new(
			new CommandDefinition("set", "Creates or updates a saved AI client profile.", [
				new("--id", "Required client nickname."),
				new("--provider", "openai, openai-compatible, or claude."),
				new("--model", "Model identifier, e.g. gpt-4.1-mini or claude-sonnet-4-6."),
				new("--base-url", "Host URL or API prefix; defaults per provider when omitted on a new profile."),
				new("--api-key", "Literal API key; saved in the global profile file."),
				new("--api-key-env", "Environment variable containing the API key, e.g. ANTHROPIC_API_KEY."),
				new("--organization", "Optional organization ID; empty clears it."),
				new("--project", "Optional project ID; empty clears it."),
				new("--system-prompt", "System prompt; quote values containing spaces.")
			]),
			(self, command, ct) => self.SetAsync(command, ct)),

		new(
			new CommandDefinition("list", "Lists saved AI client profiles without displaying keys.", []),
			(self, _, ct) => self.ListAsync(ct)),

		new(
			new CommandDefinition("use", "Sets the default AI client for ask.", [
				new("--id", "Saved client nickname.")
			]),
			(self, command, ct) => self.UseAsync(command, ct)),

		new(
			new CommandDefinition("models", "Lists model IDs a provider currently offers.", [
				new("--id", "Saved client nickname to query; omit to use --provider directly."),
				new("--provider", "openai, openai-compatible, or claude; required without --id."),
				new("--base-url", "Host URL or API prefix; defaults per provider when omitted."),
				new("--api-key", "Literal API key for this query only; not saved."),
				new("--api-key-env", "Environment variable containing the API key for this query.")
			]),
			(self, command, ct) => self.ModelsAsync(command, ct))
	];

	private static readonly IReadOnlyDictionary<string, Handler> Handlers =
		Entries.ToDictionary(
			entry => entry.Definition.Text,
			entry => entry.Run,
			StringComparer.OrdinalIgnoreCase);

	public string Name => "ai";

	public string Description => "AI clients: ask questions and manage saved client profiles.";

	public IReadOnlyList<CommandDefinition> Commands { get; } =
		[.. Entries.Select(entry => entry.Definition)];

	public Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		return Handlers.TryGetValue(command.Text, out var handler)
			? handler(this, command, cancellationToken)
			: throw new InvalidOperationException(
				$"AiCommandLayer cannot handle '{command.Text}'.");
	}

	// While control is transferred here, anything that isn't a command
	// goes to the default client as a prompt - the layer's equivalent of
	// typing ask --prompt="..." without the ceremony.
	public async Task<DtResult?> HandleTextAsync(
		string text,
		CancellationToken cancellationToken) {

		var answer = await aiClient.AskAsync(
			null,
			[new ChatMessage("user", text)],
			cancellationToken);

		output.WriteLine(answer);
		return DtResult.Continue();
	}

	private async Task<DtResult> AskAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var prompt = command.Required("--prompt");
		var messages = new List<ChatMessage>();

		if (command.Flags.TryGetValue("--file", out var filePath)) {
			var resolved = currentFolder.Resolve(filePath);

			if (!File.Exists(resolved)) {
				throw new CommandValidationException($"No such file: {resolved}");
			}

			var content = await File.ReadAllTextAsync(resolved, cancellationToken);

			messages.Add(new ChatMessage(
				"user",
				$"[File: {resolved}]{Environment.NewLine}{content}"));
		}

		messages.Add(new ChatMessage("user", prompt));

		var clientId = command.Flags.TryGetValue("--id", out var id) ? id.Trim() : null;
		var answer = await aiClient.AskAsync(clientId, messages, cancellationToken);

		output.WriteLine(answer);

		if (command.Flags.TryGetValue("--output", out var outputPath)) {
			var resolvedOutput = currentFolder.Resolve(outputPath);
			await File.WriteAllTextAsync(resolvedOutput, answer, cancellationToken);
			output.WriteLine();
			output.WriteLine($"Saved to {resolvedOutput}.");
		}

		return DtResult.Continue();
	}

	private async Task<DtResult> SetAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var id = command.Required("--id").Trim();
		ValidateId(id);

		if (command.Flags.ContainsKey("--api-key") && command.Flags.ContainsKey("--api-key-env")) {
			throw new CommandValidationException(
				"Use either --api-key or --api-key-env, not both.");
		}

		var profile = await clientStore.LoadAsync(id, cancellationToken)
			?? new AiClientProfile { Id = id };

		if (command.Flags.TryGetValue("--provider", out var provider)) {
			var previousDefaultUrl = clientStore.DefaultBaseUrl(profile.Provider);
			var newProvider = provider.Trim().ToLowerInvariant();

			profile.Provider = newProvider;

			// An unmodified default host follows the provider switch; a
			// URL the user customized (e.g. a local server) is left alone.
			if (!command.Flags.ContainsKey("--base-url")
				&& string.Equals(
					profile.BaseUrl.TrimEnd('/'),
					previousDefaultUrl.TrimEnd('/'),
					StringComparison.OrdinalIgnoreCase)) {

				profile.BaseUrl = clientStore.DefaultBaseUrl(newProvider);
			}
		}

		if (command.Flags.TryGetValue("--model", out var model)) {
			profile.Model = model.Trim();
		}

		if (command.Flags.TryGetValue("--base-url", out var baseUrl)) {
			profile.BaseUrl = baseUrl.Trim();
		}

		if (command.Flags.TryGetValue("--api-key", out var key)) {
			profile.ApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
			profile.ApiKeyEnvironmentVariable = null;
		}

		if (command.Flags.TryGetValue("--api-key-env", out var keyVariable)) {
			profile.ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(keyVariable)
				? null
				: keyVariable.Trim();

			profile.ApiKey = null;
		}

		if (command.Flags.TryGetValue("--organization", out var organization)) {
			profile.OrganizationId = string.IsNullOrWhiteSpace(organization)
				? null
				: organization.Trim();
		}

		if (command.Flags.TryGetValue("--project", out var project)) {
			profile.ProjectId = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
		}

		if (command.Flags.TryGetValue("--system-prompt", out var systemPrompt)) {
			profile.SystemPrompt = systemPrompt;
		}

		if (!clientStore.SupportsProvider(profile.Provider)) {
			throw new CommandValidationException(
				$"Unsupported provider. Available: " +
				$"{string.Join(", ", clientStore.ProviderNames)}.");
		}

		AiHttp.ValidateProfile(profile);
		await clientStore.SaveAsync(profile, cancellationToken);

		output.WriteLine($"Saved AI client '{profile.Id}' ({profile.Provider}, {profile.Model}).");
		return DtResult.Continue();
	}

	private async Task<DtResult> ListAsync(CancellationToken cancellationToken) {
		var clients = await clientStore.LoadAllAsync(cancellationToken);

		if (clients.Count == 0) {
			output.WriteLine("No saved AI clients. Use /ai set to create one.");
			return DtResult.Continue();
		}

		var state = await stateStore.LoadAsync(cancellationToken);

		foreach (var profile in clients.OrderBy(
			client => client.Id,
			StringComparer.OrdinalIgnoreCase)) {

			var marker = string.Equals(
				profile.Id,
				state.DefaultAiClientId,
				StringComparison.OrdinalIgnoreCase)
					? " [default]"
					: "";

			var credential = !string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable)
				? $"environment: {profile.ApiKeyEnvironmentVariable}"
				: string.IsNullOrWhiteSpace(profile.ApiKey)
					? "no saved key"
					: "saved key";

			output.WriteLine(
				$"{profile.Id}{marker} | {profile.Provider} | " +
				$"{profile.Model} | {credential}");
		}

		return DtResult.Continue();
	}

	private async Task<DtResult> UseAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var id = command.Required("--id").Trim();

		var profile = await clientStore.LoadAsync(id, cancellationToken)
			?? throw new CommandValidationException(
				$"AI client '{id}' not found. Use /ai list.");

		var state = await stateStore.LoadAsync(cancellationToken);
		state.DefaultAiClientId = profile.Id;
		await stateStore.SaveAsync(state, cancellationToken);

		output.WriteLine($"Default AI client: {profile.Id}.");
		return DtResult.Continue();
	}

	private async Task<DtResult> ModelsAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		AiClientProfile profile;

		if (command.Flags.TryGetValue("--id", out var id)) {
			profile = await clientStore.ResolveAsync(id.Trim(), cancellationToken);
		} else if (command.Flags.ContainsKey("--provider")) {
			var provider = command.Required("--provider").Trim().ToLowerInvariant();

			if (!clientStore.SupportsProvider(provider)) {
				throw new CommandValidationException(
					$"Unsupported provider. Available: " +
					$"{string.Join(", ", clientStore.ProviderNames)}.");
			}

			if (command.Flags.ContainsKey("--api-key") && command.Flags.ContainsKey("--api-key-env")) {
				throw new CommandValidationException(
					"Use either --api-key or --api-key-env, not both.");
			}

			// Built ad hoc for this one query - never saved to clients.json.
			profile = new AiClientProfile {
				Id = "(query)",
				Provider = provider,
				BaseUrl = command.Flags.TryGetValue("--base-url", out var baseUrl)
					? baseUrl.Trim()
					: clientStore.DefaultBaseUrl(provider),
				ApiKey = command.Flags.TryGetValue("--api-key", out var key)
					? key.Trim()
					: null,
				ApiKeyEnvironmentVariable = command.Flags.TryGetValue(
					"--api-key-env",
					out var keyVariable)
						? keyVariable.Trim()
						: null
			};
		} else {
			profile = await clientStore.ResolveAsync(null, cancellationToken);
		}

		var models = await aiClient.ListModelsAsync(profile, cancellationToken);

		if (models.Count == 0) {
			output.WriteLine("The provider returned no models.");
			return DtResult.Continue();
		}

		output.WriteLine($"{models.Count} model(s) from {profile.Provider}:");

		foreach (var model in models) {
			output.WriteLine($"  {model}");
		}

		return DtResult.Continue();
	}

	private static void ValidateId(string id) {
		if (id.Length is < 1 or > 80
			|| id.Any(character =>
				!char.IsLetterOrDigit(character)
				&& character is not ('-' or '_' or '.'))) {

			throw new CommandValidationException(
				"Client IDs must contain 1-80 letters, digits, dots, " +
				"hyphens, or underscores.");
		}
	}
}
