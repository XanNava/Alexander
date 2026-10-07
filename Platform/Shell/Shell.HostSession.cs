using System.Text;
using System.Threading.Channels;

using Alexander.Contracts;
using Alexander.Graphics;
using Alexander.Hosting;

namespace Alexander.Shell;

// Host mode: the Shell run by Alexander.Gui (with --host) instead of a
// person at a console. The Shell still does everything it always does -
// builds Core, loads it through CoreHost, handles /rebuild - but talks to
// the GUI over stdin/stdout (Alexander.Hosting protocol) instead of
// drawing a prompt:
//
//   * Anything written to the console - build output included - becomes
//     a progress message until Core is ready, output after that.
//   * Commands run one at a time on a command lane; service views render
//     on their own view lane, so a long command never freezes a view.
//   * Service views live here, next to Core; the GUI gets their drawings
//     as display lists (RecordingGraphics) and sends input back.
//
// Wiring (in Program.Main, before anything touches the console):
//
//   if (HostSession.IsRequested(args)) {
//       await using var session = HostSession.Attach();
//       coreHost.Output = session.Output;
//       session.ReportStage(HostStages.Building, "Building Core…");
//       ... your existing build + coreHost.LoadAsync(...), token: session.Stopping ...
//       ... on failure: session.ReportStage(HostStages.Failed, message); return 1;
//       return await session.RunAsync(coreHost, (progress, token) =>
//           ... your existing rebuild: build, then coreHost.SwapAsync(...) ...);
//   }
//
// Don't call StartAsync on Core yourself in host mode - RunAsync does it,
// for the first generation and after every rebuild.
internal sealed class HostSession : IOutputSink, IAsyncDisposable {
	private const string RenderOne = "~renderOne";
	private const string RenderAll = "~renderAll";

	private sealed record Work(HostMessage Message, TaskCompletionSource? Done = null, bool Echo = false);

	private sealed class OpenView(long instance, string viewId) {
		public long Instance { get; } = instance;
		public string ViewId { get; } = viewId;
		public double Width { get; set; } = 400;
		public double Height { get; set; } = 300;
		public ServiceView? View { get; set; }
		public Timer? Refresh { get; set; }

		// 1 while a render is queued, so a burst of Invalidate calls
		// (every pointer move) collapses into one frame.
		public int RenderQueued;
	}

	private readonly object writeLock = new();
	private readonly StreamWriter writer;
	private readonly StreamReader reader;
	private readonly CancellationTokenSource stopping = new();

	private readonly Channel<Work> commands =
		Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });

	private readonly Channel<HostMessage> viewWork =
		Channel.CreateUnbounded<HostMessage>(new UnboundedChannelOptions { SingleReader = true });

	// Held by the view lane per operation, and by a rebuild for its whole
	// duration (views are disposed and recreated around the swap).
	private readonly SemaphoreSlim viewGate = new(1, 1);

	// Only touched under viewGate.
	private readonly Dictionary<long, OpenView> views = new();

	private volatile bool ready;
	private volatile TextMetrics metrics = TextMetrics.Approximate;
	private volatile ViewTheme theme = ViewTheme.Dark;

	private CoreHost? host;
	private Func<IProgress<string>, CancellationToken, Task>? rebuild;

	private HostSession(Stream input, Stream output) {
		reader = new StreamReader(input, new UTF8Encoding(false));
		writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

		// Start reading at once: the GUI's init (font metrics) and any
		// commands typed during the build queue up until Core is ready.
		_ = Task.Run(ReadLoopAsync);
	}

	public static bool IsRequested(IEnumerable<string> args) {
		return args.Contains(HostProtocol.HostArgument, StringComparer.OrdinalIgnoreCase);
	}

	// Takes over stdin/stdout for the protocol and redirects Console.Out /
	// Console.Error into it. Call before anything else writes to the console.
	public static HostSession Attach() {
		var session = new HostSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
		Console.SetOut(new ForwardingWriter(session, isError: false));
		Console.SetError(new ForwardingWriter(session, isError: true));
		return session;
	}

	// Cancelled when the GUI asks the Shell to stop or goes away.
	public CancellationToken Stopping => stopping.Token;

	// Give this to CoreHost.Output.
	public IOutputSink Output => this;

	public void ReportStage(string stage, string? text = null, bool isError = false) {
		Send(new HostMessage { Type = HostMessageTypes.Progress, Stage = stage, Text = text, IsError = isError });
	}

	// Runs until the GUI disconnects or Core asks to exit. rebuildCore is
	// the Shell's own rebuild: build, then coreHost.SwapAsync - progress
	// lines go to the GUI.
	public async Task<int> RunAsync(
		CoreHost coreHost,
		Func<IProgress<string>, CancellationToken, Task> rebuildCore) {

		host = coreHost;
		rebuild = rebuildCore;

		ReportStage(HostStages.Loading, "Starting Core…");

		try {
			await StartCurrentCoreAsync();
		}
		catch (Exception exception) when (exception is not OperationCanceledException) {
			ReportStage(HostStages.Failed, $"Core failed to start: {exception.Message}", isError: true);
		}

		ready = true;
		ReportStage(HostStages.Ready);
		SendState();

		var commandLane = Task.Run(CommandLaneAsync);
		var viewLane = Task.Run(ViewLaneAsync);

		try {
			await Task.Delay(Timeout.Infinite, stopping.Token);
		}
		catch (OperationCanceledException) {
			// Shutting down.
		}

		commands.Writer.TryComplete();
		viewWork.Writer.TryComplete();
		await Task.WhenAny(Task.WhenAll(commandLane, viewLane), Task.Delay(TimeSpan.FromSeconds(3)));

		await viewGate.WaitAsync();

		try {
			foreach (var view in views.Values) {
				DisposeView(view);
			}

			views.Clear();
		}
		finally {
			viewGate.Release();
		}

		return 0;
	}

	void IOutputSink.WriteLine(string text, bool isError) {
		Send(new HostMessage {
			Type = ready ? HostMessageTypes.Output : HostMessageTypes.Progress,
			Text = text,
			IsError = isError
		});
	}

	public ValueTask DisposeAsync() {
		stopping.Cancel();
		return ValueTask.CompletedTask;
	}

	// --- reading -----------------------------------------------------

	private async Task ReadLoopAsync() {
		try {
			while (await reader.ReadLineAsync(stopping.Token) is { } line) {
				if (HostProtocol.TryParse(line) is { } message) {
					Dispatch(message);
				}
			}
		}
		catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) {
			// Stopping, or the GUI closed the pipe.
		}

		// stdin closed: the GUI is gone, so stop.
		stopping.Cancel();
	}

	private void Dispatch(HostMessage message) {
		switch (message.Type) {
			case HostMessageTypes.Init:
				if (message.Metrics is { } measured) {
					metrics = measured;
				}

				if (message.Theme is { } themeName) {
					theme = themeName == "light" ? ViewTheme.Light : ViewTheme.Dark;
				}

				viewWork.Writer.TryWrite(new HostMessage { Type = RenderAll });
				break;

			case HostMessageTypes.Input:
				commands.Writer.TryWrite(new Work(message));
				break;

			// Answered straight away, even mid-command: completion only
			// reads the catalog.
			case HostMessageTypes.Complete:
				Send(new HostMessage {
					Type = HostMessageTypes.Completions,
					Id = message.Id,
					Items = Completions(message.Text ?? "")
				});
				break;

			case HostMessageTypes.OpenView:
			case HostMessageTypes.ResizeView:
			case HostMessageTypes.ViewInput:
			case HostMessageTypes.CloseView:
				viewWork.Writer.TryWrite(message);
				break;

			case HostMessageTypes.Shutdown:
				stopping.Cancel();
				break;
		}
	}

	private List<string> Completions(string text) {
		try {
			return host?.CurrentOrNull?.GetCompletions(text).ToList() ?? [];
		}
		catch {
			return [];
		}
	}

	// --- command lane -----------------------------------------------

	private async Task CommandLaneAsync() {
		await foreach (var work in commands.Reader.ReadAllAsync()) {
			try {
				await RunCommandAsync(work);
				work.Done?.TrySetResult();
			}
			catch (Exception exception) {
				work.Done?.TrySetException(exception);
			}
		}
	}

	private async Task RunCommandAsync(Work work) {
		var text = work.Message.Text ?? "";

		if (work.Echo) {
			Send(new HostMessage { Type = HostMessageTypes.Echo, Text = text });
		}

		if (host?.CurrentOrNull is not { } core) {
			Send(new HostMessage { Type = HostMessageTypes.Error, Id = work.Message.Id, Text = "Core isn't loaded." });
			return;
		}

		DtResult result;

		try {
			result = await core.HandleInputAsync(text, stopping.Token);
		}
		catch (OperationCanceledException) {
			return;
		}
		catch (Exception exception) {
			Send(new HostMessage { Type = HostMessageTypes.Error, Id = work.Message.Id, Text = exception.Message });
			return;
		}

		Send(new HostMessage {
			Type = HostMessageTypes.Result,
			Id = work.Message.Id,
			Action = result.Action.ToString().ToLowerInvariant(),
			Text = result.Message
		});

		switch (result.Action) {
			case DtAction.Exit:
				stopping.Cancel();
				return;

			case DtAction.Rebuild:
				await RebuildAsync();
				break;
		}

		SendState();
	}

	// The Shell's rebuild, unchanged - the GUI just sees progress. Views
	// hold types from the old generation, so they're disposed before the
	// swap and recreated from the new Core after it (same instance ids, so
	// the GUI's windows stay open).
	private async Task RebuildAsync() {
		if (host is null || rebuild is null) {
			return;
		}

		ready = false;
		ReportStage(HostStages.Building, "Rebuilding Core…");

		await viewGate.WaitAsync();
		var succeeded = false;
		List<OpenView> reopened;

		try {
			foreach (var view in views.Values) {
				DisposeView(view);
			}

			var before = host.CurrentOrNull;

			try {
				await rebuild(new LineProgress(this), stopping.Token);
				succeeded = true;
			}
			catch (Exception exception) when (exception is not OperationCanceledException) {
				ReportStage(HostStages.Failed, $"Rebuild failed: {exception.Message} The current Core keeps running.", isError: true);
			}

			if (succeeded && !ReferenceEquals(before, host.CurrentOrNull) && host.CurrentOrNull is not null) {
				ReportStage(HostStages.Loading, "Starting the new Core…");

				try {
					await StartCurrentCoreAsync();
				}
				catch (Exception exception) when (exception is not OperationCanceledException) {
					ReportStage(HostStages.Failed, $"The new Core failed to start: {exception.Message}", isError: true);
				}
			}

			foreach (var view in views.Values.ToList()) {
				CreateView(view);
			}

			reopened = views.Values.ToList();
		}
		finally {
			ready = true;
			viewGate.Release();
		}

		ReportStage(HostStages.Ready, succeeded ? "Core rebuilt and reloaded." : null);

		foreach (var view in reopened) {
			QueueRender(view);
		}
	}

	private async Task StartCurrentCoreAsync() {
		var banner = await host!.Current.StartAsync(stopping.Token);

		if (!string.IsNullOrEmpty(banner)) {
			Send(new HostMessage { Type = HostMessageTypes.Output, Text = banner });
		}
	}

	private void SendState() {
		if (host?.CurrentOrNull is not { } core) {
			return;
		}

		var info = core as ISessionInfo;
		var provider = core as IServiceViewProvider;

		Send(new HostMessage {
			Type = HostMessageTypes.State,
			State = new SessionState {
				Prompt = core.PromptLabel,
				Folder = info?.CurrentFolder ?? "",
				Layer = info?.ActiveLayer,
				Views = provider?.Views
					.Select(view => new ViewDescriptor(view.Id, view.Title, view.Description))
					.ToList() ?? []
			}
		});
	}

	// --- view lane ---------------------------------------------------

	private async Task ViewLaneAsync() {
		await foreach (var message in viewWork.Reader.ReadAllAsync()) {
			await viewGate.WaitAsync();

			try {
				HandleView(message);
			}
			catch (Exception exception) {
				Send(new HostMessage { Type = HostMessageTypes.Output, Text = $"Service view error: {exception.Message}", IsError = true });
			}
			finally {
				viewGate.Release();
			}
		}
	}

	private void HandleView(HostMessage message) {
		OpenView? view;

		switch (message.Type) {
			case HostMessageTypes.OpenView:
				view = new OpenView(message.ViewInstance, message.ViewId ?? "") {
					Width = message.Width,
					Height = message.Height
				};

				views[view.Instance] = view;
				CreateView(view);
				Render(view);
				break;

			case HostMessageTypes.ResizeView when views.TryGetValue(message.ViewInstance, out view):
				view.Width = message.Width;
				view.Height = message.Height;
				Render(view);
				break;

			case HostMessageTypes.ViewInput when views.TryGetValue(message.ViewInstance, out view) && view.View is { } serviceView:
				Deliver(serviceView, message);
				break;

			case HostMessageTypes.CloseView when views.Remove(message.ViewInstance, out view):
				DisposeView(view);
				break;

			case RenderOne when views.TryGetValue(message.ViewInstance, out view):
				Interlocked.Exchange(ref view.RenderQueued, 0);
				Render(view);
				break;

			case RenderAll:
				foreach (var each in views.Values) {
					Render(each);
				}

				break;
		}
	}

	private void CreateView(OpenView view) {
		view.View = null;

		var provider = host?.CurrentOrNull as IServiceViewProvider;
		var offered = provider?.Views.Any(info =>
			string.Equals(info.Id, view.ViewId, StringComparison.OrdinalIgnoreCase)) == true;

		if (!offered) {
			views.Remove(view.Instance);
			Send(new HostMessage {
				Type = HostMessageTypes.ViewClosed,
				ViewInstance = view.Instance,
				Text = $"'{view.ViewId}' isn't offered by this Core."
			});

			return;
		}

		try {
			var serviceView = provider!.CreateView(view.ViewId, new ViewHost(this));
			serviceView.Invalidated += (_, _) => QueueRender(view);
			view.View = serviceView;

			if (serviceView.RefreshInterval is { } interval) {
				view.Refresh = new Timer(_ => QueueRender(view), null, interval, interval);
			}
		}
		catch (Exception exception) {
			views.Remove(view.Instance);
			Send(new HostMessage {
				Type = HostMessageTypes.ViewClosed,
				ViewInstance = view.Instance,
				Text = exception.Message
			});
		}
	}

	private void Render(OpenView view) {
		if (view.View is not { } serviceView) {
			return;
		}

		var size = new Size(Math.Max(0, view.Width), Math.Max(0, view.Height));
		var graphics = new RecordingGraphics(size, theme, metrics);

		try {
			serviceView.Render(graphics);
		}
		catch (Exception exception) {
			graphics = new RecordingGraphics(size, theme, metrics);
			graphics.Clear(theme.Background);
			graphics.DrawText($"View error: {exception.Message}", new Point(12, 12), theme.Danger);
		}

		Send(new HostMessage {
			Type = HostMessageTypes.Frame,
			ViewInstance = view.Instance,
			Text = serviceView.Title,
			Ops = [.. graphics.Ops]
		});
	}

	private static void Deliver(ServiceView view, HostMessage message) {
		var modifiers = (KeyModifiers)message.Modifiers;
		var position = new Point(message.X, message.Y);
		var button = Enum.TryParse<PointerButton>(message.Button, ignoreCase: true, out var parsed)
			? parsed
			: PointerButton.None;

		switch (message.Text) {
			case ViewInputKinds.Move:
				view.OnPointerMove(new PointerEvent(position, button, modifiers));
				break;
			case ViewInputKinds.Down:
				view.OnPointerDown(new PointerEvent(position, button, modifiers));
				break;
			case ViewInputKinds.Up:
				view.OnPointerUp(new PointerEvent(position, button, modifiers));
				break;
			case ViewInputKinds.Leave:
				view.OnPointerLeave();
				break;
			case ViewInputKinds.Wheel:
				view.OnWheel(new WheelEvent(position, message.Delta, modifiers));
				break;
			case ViewInputKinds.Key:
				view.OnKeyDown(new KeyEvent(message.Key ?? "", modifiers));
				break;
		}
	}

	// Safe from any thread.
	private void QueueRender(OpenView view) {
		if (Interlocked.Exchange(ref view.RenderQueued, 1) == 0) {
			viewWork.Writer.TryWrite(new HostMessage { Type = RenderOne, ViewInstance = view.Instance });
		}
	}

	private static void DisposeView(OpenView view) {
		view.Refresh?.Dispose();
		view.Refresh = null;

		try {
			view.View?.Dispose();
		}
		catch {
			// A view failing to clean up mustn't stop the others.
		}

		view.View = null;
	}

	// --- writing -----------------------------------------------------

	private void Send(HostMessage message) {
		var line = HostProtocol.Serialize(message);

		lock (writeLock) {
			try {
				writer.WriteLine(line);
			}
			catch (Exception exception) when (exception is IOException or ObjectDisposedException) {
				// The GUI is gone.
				stopping.Cancel();
			}
		}
	}

	private sealed class LineProgress(HostSession session) : IProgress<string> {
		public void Report(string value) {
			session.Send(new HostMessage { Type = HostMessageTypes.Progress, Stage = HostStages.Building, Text = value });
		}
	}

	// What a service view in this Shell can ask for: commands it runs go
	// through the command lane like typed input, echoed in the GUI.
	private sealed class ViewHost(HostSession session) : IViewHost {
		public string CurrentFolder =>
			(session.host?.CurrentOrNull as ISessionInfo)?.CurrentFolder ?? "";

		public Task RunCommandAsync(string input) {
			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

			session.commands.Writer.TryWrite(new Work(
				new HostMessage { Type = HostMessageTypes.Input, Text = input },
				done,
				Echo: true));

			return done.Task;
		}
	}

	// Console.Out / Console.Error in host mode: whole lines become
	// progress (while building/loading) or output messages.
	private sealed class ForwardingWriter(HostSession session, bool isError) : TextWriter {
		private readonly StringBuilder line = new();

		public override Encoding Encoding => Encoding.UTF8;

		public override void Write(char value) {
			lock (line) {
				if (value == '\n') {
					var text = line.ToString();
					line.Clear();
					((IOutputSink)session).WriteLine(text, isError);
				} else if (value != '\r') {
					line.Append(value);
				}
			}
		}

		public override void Write(string? value) {
			if (value is null) {
				return;
			}

			foreach (var character in value) {
				Write(character);
			}
		}

		public override void WriteLine(string? value) {
			Write(value);
			Write('\n');
		}

		// Partial lines wait for their newline; flushing them early would
		// split one line into several messages.
		public override void Flush() { }
	}
}
