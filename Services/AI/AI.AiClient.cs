using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Alexander.Shared;

namespace Alexander.Services.AI;

//
// AI - START
#region AI

public sealed class AiClient(
	IAiClientStore clientStore,
	IEnumerable<IAiProvider> providers,
	IOutputWriter output) : IAiClient {

	private readonly Dictionary<string, IAiProvider> providersByName =
		providers.ToDictionary(
			provider => provider.Name,
			StringComparer.OrdinalIgnoreCase);

	public async Task<string> AskAsync(
		string? clientId,
		IReadOnlyList<ChatMessage> messages,
		CancellationToken cancellationToken) {

		var profile = await clientStore.ResolveAsync(clientId, cancellationToken);
		var provider = ResolveProvider(profile);

		AiHttp.ValidateProfile(profile);

		try {
			return await provider.SendAsync(profile, messages, cancellationToken);
		}
		catch (AiRequestException exception) {
			await ProviderGuidanceWriter.PrintIfAvailableAsync(
				output, provider, profile, exception.StatusCode);
			throw;
		}
	}

	public async Task<IReadOnlyList<string>> ListModelsAsync(
		AiClientProfile profile,
		CancellationToken cancellationToken) {

		var provider = ResolveProvider(profile);

		AiHttp.ValidateProfile(profile);

		try {
			return await provider.ListModelsAsync(profile, cancellationToken);
		}
		catch (AiRequestException exception) {
			await ProviderGuidanceWriter.PrintIfAvailableAsync(
				output, provider, profile, exception.StatusCode);
			throw;
		}
	}

	private IAiProvider ResolveProvider(AiClientProfile profile) {
		if (!providersByName.TryGetValue(profile.Provider, out var provider)) {
			throw new InvalidOperationException(
				$"Unsupported provider '{profile.Provider}'. " +
				$"Registered providers: {string.Join(", ", providersByName.Keys)}.");
		}

		return provider;
	}
}

public sealed class AiClientProfile {
	public string Id { get; set; } = "";
	public string Provider { get; set; } = "openai";
	public string BaseUrl { get; set; } = "https://api.openai.com";
	public string? ApiKey { get; set; }
	public string? ApiKeyEnvironmentVariable { get; set; }
	public string Model { get; set; } = "gpt-4.1-mini";
	public string? OrganizationId { get; set; }
	public string? ProjectId { get; set; }

	public string SystemPrompt { get; set; } =
		"You are a concise, helpful assistant, callable from the command " +
		"line of a folder-automation tool. When given file content, focus " +
		"your answer on it.";
}

public interface IAiClient {
	// clientId null means "use the configured default" (AppState.DefaultAiClientId).
	Task<string> AskAsync(
		string? clientId,
		IReadOnlyList<ChatMessage> messages,
		CancellationToken cancellationToken);

	// profile is passed directly (not a clientId) so the caller can query
	// a provider ad hoc, before ever saving a client profile for it.
	Task<IReadOnlyList<string>> ListModelsAsync(
		AiClientProfile profile,
		CancellationToken cancellationToken);
}

public interface IAiProvider {
	string Name { get; }

	// Auto-fills a sensible host when a profile's --provider changes.
	// Owned by the provider itself, not the profile data model, so
	// adding a provider is a single self-contained addition.
	string DefaultBaseUrl { get; }

	Task<string> SendAsync(
		AiClientProfile profile,
		IReadOnlyList<ChatMessage> messages,
		CancellationToken cancellationToken);

	Task<IReadOnlyList<string>> ListModelsAsync(
		AiClientProfile profile,
		CancellationToken cancellationToken);

	// Returns step-by-step guidance for a failed HTTP status this provider
	// has something specific to say about (401, 429, a 5xx outage, ...),
	// or null for anything it has no special advice for.
	StatusGuidance? DescribeStatus(HttpStatusCode statusCode);
}

// One status code's worth of guidance. ApiKeyFlagExample is null for
// non-credential problems (a 429 or 500 isn't fixed by rotating a key),
// in which case the writer skips the "/ai set" step entirely.
public sealed record StatusGuidance(
	string Explanation,
	string FirstStep,
	string? ApiKeyFlagExample);

public sealed record AiClientProfileFile(
	IReadOnlyList<AiClientProfile> Clients);

public interface IAiClientStore {
	IReadOnlyList<string> ProviderNames { get; }
	bool SupportsProvider(string name);
	string DefaultBaseUrl(string provider);

	Task<IReadOnlyList<AiClientProfile>> LoadAllAsync(
		CancellationToken cancellationToken);

	Task<AiClientProfile?> LoadAsync(
		string id,
		CancellationToken cancellationToken);

	// id null resolves AppState.DefaultAiClientId instead; throws a clear
	// message if neither is set, pointing at /ai set and /ai use.
	Task<AiClientProfile> ResolveAsync(
		string? id,
		CancellationToken cancellationToken);

	Task SaveAsync(AiClientProfile profile, CancellationToken cancellationToken);
}

public sealed class AiClientStore(
	AppPaths paths,
	IStateStore stateStore,
	IEnumerable<IAiProvider> providers) : IAiClientStore {

	private readonly Dictionary<string, IAiProvider> providersByName =
		providers.ToDictionary(
			provider => provider.Name,
			StringComparer.OrdinalIgnoreCase);

	public IReadOnlyList<string> ProviderNames { get; } =
		providers.Select(provider => provider.Name).ToArray();

	public bool SupportsProvider(string name) {
		return ProviderNames.Contains(name, StringComparer.OrdinalIgnoreCase);
	}

	public string DefaultBaseUrl(string provider) {
		return providersByName.TryGetValue(provider, out var match)
			? match.DefaultBaseUrl
			: "https://api.openai.com";
	}

	public async Task<IReadOnlyList<AiClientProfile>> LoadAllAsync(
		CancellationToken cancellationToken) {

		var file = await JsonFiles.ReadAsync<AiClientProfileFile>(
			paths.ClientsFile,
			cancellationToken);

		return file?.Clients ?? Array.Empty<AiClientProfile>();
	}

	public async Task<AiClientProfile?> LoadAsync(
		string id,
		CancellationToken cancellationToken) {

		var clients = await LoadAllAsync(cancellationToken);

		return clients.FirstOrDefault(client =>
			string.Equals(client.Id, id, StringComparison.OrdinalIgnoreCase));
	}

	public async Task<AiClientProfile> ResolveAsync(
		string? id,
		CancellationToken cancellationToken) {

		var resolvedId = id;

		if (string.IsNullOrWhiteSpace(resolvedId)) {
			var state = await stateStore.LoadAsync(cancellationToken);
			resolvedId = state.DefaultAiClientId;
		}

		if (string.IsNullOrWhiteSpace(resolvedId)) {
			throw new InvalidOperationException(
				"No AI client is configured. Use /ai set to create one, " +
				"then /ai use to make it the default (or pass --id explicitly).");
		}

		return await LoadAsync(resolvedId, cancellationToken)
			?? throw new InvalidOperationException(
				$"AI client '{resolvedId}' not found. Use /ai list.");
	}

	public async Task SaveAsync(
		AiClientProfile profile,
		CancellationToken cancellationToken) {

		var clients = (await LoadAllAsync(cancellationToken)).ToList();

		var index = clients.FindIndex(client =>
			string.Equals(client.Id, profile.Id, StringComparison.OrdinalIgnoreCase));

		if (index >= 0) {
			clients[index] = profile;
		} else {
			clients.Add(profile);
		}

		await JsonFiles.WriteAsync(
			paths.ClientsFile,
			new AiClientProfileFile(clients),
			cancellationToken);
	}
}

// Shared by every provider's DescribeStatus: check the specific codes it
// has something to say about, then fall back to a generic "this is a
// server-side outage" guidance for any other 5xx.
internal static class StatusGuidanceLookup {
	public static StatusGuidance? Resolve(
		IReadOnlyDictionary<HttpStatusCode, StatusGuidance> specific,
		StatusGuidance? serverErrorFallback,
		HttpStatusCode statusCode) {

		if (specific.TryGetValue(statusCode, out var guidance)) {
			return guidance;
		}

		return (int)statusCode >= 500 ? serverErrorFallback : null;
	}
}

public sealed class OpenAiResponsesProvider(
	IHttpClientFactory httpClientFactory) : IAiProvider {

	public string Name => "openai";
	public string DefaultBaseUrl => "https://api.openai.com";

	private static readonly IReadOnlyDictionary<HttpStatusCode, StatusGuidance> Guidance =
		new Dictionary<HttpStatusCode, StatusGuidance> {
			[HttpStatusCode.Unauthorized] = new StatusGuidance(
				"OpenAI rejected the request with HTTP 401: the API key is " +
					"missing, invalid, or has been revoked.",
				"Get a current key: https://platform.openai.com/api-keys",
				"--api-key=sk-... (or --api-key-env=OPENAI_API_KEY)"),
			[HttpStatusCode.TooManyRequests] = new StatusGuidance(
				"OpenAI rejected the request with HTTP 429: you're sending " +
					"requests faster than your current rate limit or quota allows.",
				"Wait a few seconds and retry, or check your limits: " +
					"https://platform.openai.com/account/limits",
				null)
		};

	private static readonly StatusGuidance ServerErrorGuidance = new(
		"OpenAI returned a server error - this is on their end, not your " +
			"configuration.",
		"Check https://status.openai.com, then try again shortly.",
		null);

	public StatusGuidance? DescribeStatus(HttpStatusCode statusCode) {
		return StatusGuidanceLookup.Resolve(Guidance, ServerErrorGuidance, statusCode);
	}

	public async Task<string> SendAsync(
		AiClientProfile profile,
		IReadOnlyList<ChatMessage> messages,
		CancellationToken cancellationToken) {

		var history = AiHttp.BuildMessages(profile, messages);

		var payload = new {
			model = profile.Model,
			input = history.Select(message => new {
				role = message.Role,
				content = message.Content
			}).ToArray()
		};

		using var document = await AiHttp.PostAsync(
			httpClientFactory,
			profile,
			"responses",
			payload,
			cancellationToken);

		var root = document.RootElement;

		if (root.TryGetProperty("status", out var status)
			&& status.GetString() is "failed" or "cancelled" or "incomplete") {

			throw new InvalidOperationException(
				$"Provider returned response status '{status.GetString()}'.");
		}

		if (root.TryGetProperty("output_text", out var directText)
			&& directText.ValueKind == JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(directText.GetString())) {

			return directText.GetString()!.Trim();
		}

		var parts = new List<string>();

		if (root.TryGetProperty("output", out var output)
			&& output.ValueKind == JsonValueKind.Array) {

			foreach (var item in output.EnumerateArray()) {
				if (!item.TryGetProperty("content", out var contentArray)
					|| contentArray.ValueKind != JsonValueKind.Array) {

					continue;
				}

				foreach (var contentItem in contentArray.EnumerateArray()) {
					if (contentItem.TryGetProperty("text", out var text)
						&& text.ValueKind == JsonValueKind.String) {

						parts.Add(text.GetString() ?? "");
					}
				}
			}
		}

		if (parts.Count == 0) {
			throw new InvalidOperationException(
				"The provider returned no text response.");
		}

		return string.Join(Environment.NewLine, parts).Trim();
	}

	public async Task<IReadOnlyList<string>> ListModelsAsync(
		AiClientProfile profile,
		CancellationToken cancellationToken) {

		using var document = await AiHttp.GetAsync(
			httpClientFactory,
			profile,
			"models",
			cancellationToken);

		return AiHttp.ParseModelIds(document);
	}
}

// Talks to Anthropic's native Messages API (api.anthropic.com/v1/messages).
public sealed class ClaudeProvider(
	IHttpClientFactory httpClientFactory) : IAiProvider {

	// The Messages API requires max_tokens; there is no server-side default.
	private const int MaxTokens = 4096;
	private const string ApiVersion = "2023-06-01";

	public string Name => "claude";
	public string DefaultBaseUrl => "https://api.anthropic.com";

	private static readonly IReadOnlyDictionary<HttpStatusCode, StatusGuidance> Guidance =
		new Dictionary<HttpStatusCode, StatusGuidance> {
			[HttpStatusCode.Unauthorized] = new StatusGuidance(
				"Anthropic rejected the request with HTTP 401: the x-api-key " +
					"header is missing, invalid, or has been revoked.",
				"Get a current key: https://console.anthropic.com/settings/keys",
				"--api-key=sk-ant-... (or --api-key-env=ANTHROPIC_API_KEY)"),
			[HttpStatusCode.TooManyRequests] = new StatusGuidance(
				"Anthropic rejected the request with HTTP 429: you're sending " +
					"requests faster than your current rate limit allows.",
				"Wait a few seconds and retry, or check your limits: " +
					"https://console.anthropic.com/settings/limits",
				null)
		};

	private static readonly StatusGuidance ServerErrorGuidance = new(
		"Anthropic returned a server error - this is on their end, not " +
			"your configuration.",
		"Check https://status.anthropic.com, then try again shortly.",
		null);

	public StatusGuidance? DescribeStatus(HttpStatusCode statusCode) {
		return StatusGuidanceLookup.Resolve(Guidance, ServerErrorGuidance, statusCode);
	}

	public async Task<string> SendAsync(
		AiClientProfile profile,
		IReadOnlyList<ChatMessage> messages,
		CancellationToken cancellationToken) {

		var history = messages
			.Where(message => message.Role != "system")
			.Select(message => {
				var role = message.Role.ToLowerInvariant();

				if (role is not ("user" or "assistant")) {
					throw new InvalidOperationException(
						"An unsupported message role was passed to the provider.");
				}

				return new {
					role,
					content = message.Content
				};
			})
			.ToArray();

		var payload = new {
			model = profile.Model,
			max_tokens = MaxTokens,
			system = string.IsNullOrWhiteSpace(profile.SystemPrompt)
				? null
				: profile.SystemPrompt,
			messages = history
		};

		using var document = await AiHttp.PostAsync(
			httpClientFactory,
			profile,
			"messages",
			payload,
			cancellationToken,
			request => {
				request.Headers.Add("anthropic-version", ApiVersion);
			});

		var root = document.RootElement;

		if (root.TryGetProperty("stop_reason", out var stopReason)
			&& stopReason.GetString() == "refusal") {

			throw new InvalidOperationException("The provider refused this request.");
		}

		var parts = new List<string>();

		if (root.TryGetProperty("content", out var content)
			&& content.ValueKind == JsonValueKind.Array) {

			foreach (var item in content.EnumerateArray()) {
				if (item.TryGetProperty("type", out var type)
					&& type.GetString() == "text"
					&& item.TryGetProperty("text", out var text)
					&& text.ValueKind == JsonValueKind.String) {

					parts.Add(text.GetString() ?? "");
				}
			}
		}

		if (parts.Count == 0) {
			throw new InvalidOperationException(
				"The provider returned no text response.");
		}

		return string.Join(Environment.NewLine, parts).Trim();
	}

	public async Task<IReadOnlyList<string>> ListModelsAsync(
		AiClientProfile profile,
		CancellationToken cancellationToken) {

		using var document = await AiHttp.GetAsync(
			httpClientFactory,
			profile,
			"models",
			cancellationToken,
			request => {
				request.Headers.Add("anthropic-version", ApiVersion);
			});

		return AiHttp.ParseModelIds(document);
	}
}

// Supports services implementing the OpenAI Chat Completions protocol.
// Native provider APIs can be added as separate IAiProvider implementations.
public sealed class OpenAiCompatibleProvider(
	IHttpClientFactory httpClientFactory) : IAiProvider {

	public string Name => "openai-compatible";
	public string DefaultBaseUrl => "https://api.openai.com";

	private static readonly IReadOnlyDictionary<HttpStatusCode, StatusGuidance> Guidance =
		new Dictionary<HttpStatusCode, StatusGuidance> {
			[HttpStatusCode.Unauthorized] = new StatusGuidance(
				"The endpoint rejected the request with HTTP 401: the key " +
					"it expects is missing, invalid, or in a format it " +
					"doesn't recognize. Since this is a custom endpoint, " +
					"there's no single dashboard to point at.",
				"Check that endpoint's own docs for the key it expects " +
					"(some self-hosted servers need no key at all).",
				"--api-key=... (or --api-key-env=SOME_ENV_VAR)"),
			[HttpStatusCode.TooManyRequests] = new StatusGuidance(
				"The endpoint rejected the request with HTTP 429: you're " +
					"sending requests faster than it allows.",
				"Wait a few seconds and retry, or check that endpoint's " +
					"own rate-limit docs.",
				null)
		};

	private static readonly StatusGuidance ServerErrorGuidance = new(
		"The endpoint returned a server error - check whether it's up " +
			"and reachable.",
		"Check that endpoint's status page or logs, then try again shortly.",
		null);

	public StatusGuidance? DescribeStatus(HttpStatusCode statusCode) {
		return StatusGuidanceLookup.Resolve(Guidance, ServerErrorGuidance, statusCode);
	}

	public async Task<string> SendAsync(
		AiClientProfile profile,
		IReadOnlyList<ChatMessage> messages,
		CancellationToken cancellationToken) {

		var history = AiHttp.BuildMessages(profile, messages);

		var payload = new {
			model = profile.Model,
			messages = history.Select(message => new {
				role = message.Role,
				content = message.Content
			}).ToArray()
		};

		using var document = await AiHttp.PostAsync(
			httpClientFactory,
			profile,
			"chat/completions",
			payload,
			cancellationToken);

		var root = document.RootElement;

		if (root.TryGetProperty("choices", out var choices)
			&& choices.ValueKind == JsonValueKind.Array) {

			foreach (var choice in choices.EnumerateArray()) {
				if (choice.TryGetProperty("message", out var message)
					&& message.TryGetProperty("content", out var content)
					&& content.ValueKind == JsonValueKind.String
					&& !string.IsNullOrWhiteSpace(content.GetString())) {

					return content.GetString()!.Trim();
				}
			}
		}

		throw new InvalidOperationException(
			"The provider returned no text response.");
	}

	public async Task<IReadOnlyList<string>> ListModelsAsync(
		AiClientProfile profile,
		CancellationToken cancellationToken) {

		using var document = await AiHttp.GetAsync(
			httpClientFactory,
			profile,
			"models",
			cancellationToken);

		return AiHttp.ParseModelIds(document);
	}
}

// The invariant "look up guidance, print it if there is any" skeleton
// AiClient calls for every failed request, regardless of status code.
public static class ProviderGuidanceWriter {
	public static Task PrintIfAvailableAsync(
		IOutputWriter output,
		IAiProvider provider,
		AiClientProfile profile,
		HttpStatusCode statusCode) {

		var guidance = provider.DescribeStatus(statusCode);

		if (guidance is null) {
			return Task.CompletedTask;
		}

		output.WriteLine(
			$"--- {(int)statusCode} {statusCode}: {profile.Id} ({provider.Name}) ---");
		output.WriteLine(guidance.Explanation);

		if (guidance.ApiKeyFlagExample is not null) {
			output.WriteLine(AiHttp.DescribeKeySource(profile));
		}

		output.WriteLine();
		output.WriteLine("To fix it:");
		output.WriteLine($"  1. {guidance.FirstStep}");

		if (guidance.ApiKeyFlagExample is not null) {
			output.WriteLine(
				$"  2. Update this profile: /ai set --id={profile.Id} " +
				guidance.ApiKeyFlagExample);
			output.WriteLine(
				$"  3. Verify it works: /ai models --id={profile.Id}");
		}

		output.WriteLine("---");
		output.WriteLine();

		return Task.CompletedTask;
	}
}

// Carries the HTTP status code so callers can branch on it (e.g. 401 ->
// run the provider's walkthrough) without parsing the message string.
public sealed class AiRequestException(
	HttpStatusCode statusCode,
	string message) : InvalidOperationException(message) {

	public HttpStatusCode StatusCode { get; } = statusCode;
}

public static class AiHttp {
	public static void ValidateProfile(AiClientProfile profile) {
		if (string.IsNullOrWhiteSpace(profile.Model)) {
			throw new InvalidOperationException("A model is required.");
		}

		_ = Endpoint(profile.BaseUrl, "responses");
	}

	public static Uri Endpoint(string baseUrl, string resource) {
		if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttps
				&& uri.Scheme != Uri.UriSchemeHttp)
			|| !string.IsNullOrEmpty(uri.Query)
			|| !string.IsNullOrEmpty(uri.Fragment)
			|| !string.IsNullOrEmpty(uri.UserInfo)) {

			throw new InvalidOperationException(
				"Base URL must be an absolute HTTP(S) URL " +
				"without credentials, a query, or a fragment.");
		}

		var url = uri.AbsoluteUri.TrimEnd('/');

		// A host-only URL receives /v1. Explicit API paths such as /v1
		// or /openai/v1 are preserved.
		if (string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))) {
			url += "/v1";
		}

		return new Uri($"{url}/{resource}");
	}

	public static List<ChatMessage> BuildMessages(
		AiClientProfile profile,
		IReadOnlyList<ChatMessage> messages) {

		var result = new List<ChatMessage>();

		if (!string.IsNullOrWhiteSpace(profile.SystemPrompt)) {
			result.Add(new ChatMessage("system", profile.SystemPrompt));
		}

		foreach (var message in messages) {
			var role = message.Role.ToLowerInvariant();

			if (role == "system") {
				continue;
			}

			if (role is not ("user" or "assistant" or "developer")) {
				throw new InvalidOperationException(
					"An unsupported message role was passed to the provider.");
			}

			result.Add(new ChatMessage(role, message.Content));
		}

		return result;
	}

	// Never prints the key itself - only where it's supposed to come from,
	// and (for an env var) whether this process can actually see it right
	// now, which is the single most common cause of "it's set but still
	// 401s" (set in a different shell/session than the one running this).
	public static string DescribeKeySource(AiClientProfile profile) {
		if (!string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable)) {
			var isSet = !string.IsNullOrWhiteSpace(
				Environment.GetEnvironmentVariable(profile.ApiKeyEnvironmentVariable));

			return $"This profile reads its key from the environment " +
				$"variable {profile.ApiKeyEnvironmentVariable}, which is " +
				(isSet
					? "set in this process."
					: "NOT set in this process - that's likely the problem. " +
						"Setting it in another terminal doesn't reach an " +
						"already-running Alexander.");
		}

		if (!string.IsNullOrWhiteSpace(profile.ApiKey)) {
			return "This profile has a literal key saved (value hidden).";
		}

		return "This profile has no key configured at all.";
	}

	public static async Task<JsonDocument> PostAsync(
		IHttpClientFactory httpClientFactory,
		AiClientProfile profile,
		string resource,
		object payload,
		CancellationToken cancellationToken,
		Action<HttpRequestMessage>? configureRequest = null) {

		using var client = httpClientFactory.CreateClient("Ai");

		using var request = new HttpRequestMessage(
			HttpMethod.Post,
			Endpoint(profile.BaseUrl, resource)) {

			Content = JsonContent.Create(payload)
		};

		ApplyAuthentication(request, profile);
		configureRequest?.Invoke(request);

		return await SendAsync(client, request, cancellationToken);
	}

	public static async Task<JsonDocument> GetAsync(
		IHttpClientFactory httpClientFactory,
		AiClientProfile profile,
		string resource,
		CancellationToken cancellationToken,
		Action<HttpRequestMessage>? configureRequest = null) {

		using var client = httpClientFactory.CreateClient("Ai");

		using var request = new HttpRequestMessage(
			HttpMethod.Get,
			Endpoint(profile.BaseUrl, resource));

		ApplyAuthentication(request, profile);
		configureRequest?.Invoke(request);

		return await SendAsync(client, request, cancellationToken);
	}

	// Pulls the "id" field out of a { "data": [ { "id": "..." }, ... ] }
	// response - the shape both the OpenAI and Anthropic /v1/models
	// endpoints share.
	public static IReadOnlyList<string> ParseModelIds(JsonDocument document) {
		var ids = new List<string>();

		if (document.RootElement.TryGetProperty("data", out var data)
			&& data.ValueKind == JsonValueKind.Array) {

			foreach (var item in data.EnumerateArray()) {
				if (item.TryGetProperty("id", out var id)
					&& id.ValueKind == JsonValueKind.String
					&& !string.IsNullOrWhiteSpace(id.GetString())) {

					ids.Add(id.GetString()!);
				}
			}
		}

		ids.Sort(StringComparer.OrdinalIgnoreCase);
		return ids;
	}

	// Resolves the API key (literal or from an environment variable) and
	// attaches whatever headers this provider's auth scheme needs. Shared
	// by every request kind (send, list models, ...) so they can never
	// drift out of sync with each other.
	private static void ApplyAuthentication(
		HttpRequestMessage request,
		AiClientProfile profile) {

		var apiKey = profile.ApiKey;

		if (!string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable)) {
			apiKey = Environment.GetEnvironmentVariable(
				profile.ApiKeyEnvironmentVariable);

			if (string.IsNullOrWhiteSpace(apiKey)) {
				throw new InvalidOperationException(
					$"Environment variable '{profile.ApiKeyEnvironmentVariable}' " +
					"is missing or empty.");
			}
		}

		var isClaude = string.Equals(
			profile.Provider,
			"claude",
			StringComparison.OrdinalIgnoreCase);

		if (string.IsNullOrWhiteSpace(apiKey)
			&& (isClaude
				|| string.Equals(
					profile.Provider,
					"openai",
					StringComparison.OrdinalIgnoreCase))) {

			throw new InvalidOperationException(
				isClaude
					? "No API key is configured. Set ANTHROPIC_API_KEY " +
						"or configure the selected client profile."
					: "No API key is configured. Set OPENAI_API_KEY " +
						"or configure the selected client profile.");
		}

		if (isClaude) {
			// The Anthropic API authenticates via a plain header, not Bearer.
			if (!string.IsNullOrWhiteSpace(apiKey)) {
				request.Headers.Add("x-api-key", apiKey);
			}
		} else {
			if (!string.IsNullOrWhiteSpace(apiKey)) {
				request.Headers.Authorization =
					new AuthenticationHeaderValue("Bearer", apiKey);
			}

			if (!string.IsNullOrWhiteSpace(profile.OrganizationId)) {
				request.Headers.Add(
					"OpenAI-Organization",
					profile.OrganizationId);
			}

			if (!string.IsNullOrWhiteSpace(profile.ProjectId)) {
				request.Headers.Add("OpenAI-Project", profile.ProjectId);
			}
		}
	}

	private static async Task<JsonDocument> SendAsync(
		HttpClient client,
		HttpRequestMessage request,
		CancellationToken cancellationToken) {

		using var response = await client.SendAsync(request, cancellationToken);

		if (!response.IsSuccessStatusCode) {
			// Avoid echoing arbitrary provider bodies or credentials.
			throw new AiRequestException(
				response.StatusCode,
				$"AI request failed with HTTP {(int)response.StatusCode} " +
				$"({response.StatusCode}).");
		}

		var body = await response.Content.ReadAsStringAsync(cancellationToken);

		return JsonDocument.Parse(body);
	}
}

#endregion
// AI - END
//
