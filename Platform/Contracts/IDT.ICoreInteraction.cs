namespace Alexander.Contracts;

// What the shell needs to own the interactive loop itself: the prompt
// text, completion candidates for whatever's been typed so far, and
// dispatch for a finished line. Core decides everything ABOUT those -
// what the prompt says (the current folder), which commands/flags exist,
// what a command actually does to the filesystem or a running service -
// the shell just triggers it and renders the answer.
public interface ICoreInteraction {
	// Shown in the shell's prompt, e.g. the current folder's name.
	string PromptLabel { get; }

	// Completion candidates for the input typed so far, each a full
	// replacement for `input` - the shell's line editor derives the
	// "ghost" suggestion text as candidate[input.Length..]. Empty when
	// nothing is completable. Synchronous and in-memory: called on every
	// keystroke, so it must stay cheap.
	IReadOnlyList<string> GetCompletions(string input);

	// One raw line of input, already collected by the shell. Core decides
	// whether it's a known command and returns what the shell should do
	// next.
	Task<DtResult> HandleInputAsync(
		string input,
		CancellationToken cancellationToken);
}
