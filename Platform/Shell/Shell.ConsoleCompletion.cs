using System.Text;

namespace Alexander.Shell;

// Candidates come from Core (IDT.GetCompletions, called fresh on every
// keystroke), but the actual terminal drawing lives here - completion is
// terminal UI (key handling, cursor math, ghost-text rendering) and
// belongs with the shell that owns the rest of the console.
//
// Key bindings:
//   Left / Right  - move the caret through the typed text, like a normal
//                   line editor. Left always dismisses any active
//                   suggestion. Right at the true end of the text instead
//                   ACCEPTS the suggestion, up to its next space (see
//                   AcceptPartial), and immediately reloads candidates
//                   for the new, longer text, so repeated presses keep
//                   completing further one word/flag at a time.
//   Tab           - the same partial-accept as Right; Shift+Tab cycles
//                   backward instead.
//   Up / Down     - cycle the suggestion list forward / backward.
//   Shift+Space   - accept the WHOLE suggestion at once (skips the
//                   word-by-word stepping Tab/Right do), plus a trailing
//                   space.
//
// Suggestions only exist while the caret sits at the true end of the
// text - there's no sensible ghost completion to show once you've moved
// left into the middle of what you already typed.
internal static class ConsoleLineReader {
	public static string? ReadLine(
		Func<string, IReadOnlyList<string>> getCompletions) {

		try {
			return ReadInteractive(getCompletions);
		}
		catch (InvalidOperationException) {
			// Console input is redirected (piped/non-interactive) - Tab
			// completion and Console.ReadKey aren't available there.
			// Fall back to a plain, uncompleted read rather than crashing.
			return Console.ReadLine();
		}
	}

	private static string ReadInteractive(
		Func<string, IReadOnlyList<string>> getCompletions) {

		var buffer = new StringBuilder();
		var cursorIndex = 0;
		var lastRenderedLength = 0;

		// How far the ON-SCREEN cursor currently sits from the true start
		// of this input line, in characters. Renders move the terminal
		// cursor RELATIVE to wherever it currently is (never to a
		// remembered absolute row/column), so this is the only position
		// bookkeeping needed - and it stays correct even if the console
		// auto-scrolled since the last render.
		var caretOffset = 0;

		IReadOnlyList<string> candidates = [];
		var candidateIndex = 0;

		RefreshCandidates();
		Render();

		while (true) {
			var keyInfo = Console.ReadKey(intercept: true);

			switch (keyInfo.Key) {
			case ConsoleKey.Enter:
				FinishLine();
				return buffer.ToString();

			case ConsoleKey.Backspace:
				if (cursorIndex > 0) {
					buffer.Remove(cursorIndex - 1, 1);
					cursorIndex--;
					RefreshCandidates();
					Render();
				}

				break;

			case ConsoleKey.Escape:
				buffer.Clear();
				cursorIndex = 0;
				RefreshCandidates();
				Render();
				break;

			case ConsoleKey.LeftArrow:
				// Always dismisses any suggestion, even if the caret was
				// already at position 0 - moving left is the signal that
				// you're editing, not completing.
				if (cursorIndex > 0) {
					cursorIndex--;
				}

				candidates = [];
				candidateIndex = 0;
				Render();
				break;

			case ConsoleKey.RightArrow:
				if (cursorIndex < buffer.Length) {
					// Real text still ahead of the caret - just move
					// over it, same as any normal line editor.
					cursorIndex++;
					RefreshCandidates();
					Render();
				} else if (candidates.Count > 0) {
					AcceptPartial();
				}

				break;

			case ConsoleKey.UpArrow:
				if (candidates.Count > 0) {
					candidateIndex = (candidateIndex + 1) % candidates.Count;
					Render();
				}

				break;

			case ConsoleKey.DownArrow:
				if (candidates.Count > 0) {
					candidateIndex = (candidateIndex - 1 + candidates.Count) % candidates.Count;
					Render();
				}

				break;

			case ConsoleKey.Tab:
				if (keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift)) {
					// Shift+Tab just cycles backward - Up/Down do the
					// same forward/backward cycling, this is kept as an
					// alias.
					if (candidates.Count > 0) {
						candidateIndex = (candidateIndex - 1 + candidates.Count) % candidates.Count;
						Render();
					}
				} else if (candidates.Count > 0) {
					// Plain Tab completes, same as Right arrow - pressing
					// it repeatedly steps through a multi-word suggestion
					// one part at a time instead of only ever taking it
					// all at once.
					AcceptPartial();
				}

				break;

			case ConsoleKey.Spacebar
				when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift)
					&& candidates.Count > 0:

				// Unlike Tab/Right (which stop at the next space), this
				// always takes the WHOLE suggestion, plus one more
				// trailing space - the shortcut for "skip the
				// step-by-step and just accept all of it."
				buffer.Clear();
				buffer.Append(candidates[candidateIndex]);
				buffer.Append(' ');
				cursorIndex = buffer.Length;
				RefreshCandidates();
				Render();
				break;

			default:
				if (!char.IsControl(keyInfo.KeyChar)) {
					buffer.Insert(cursorIndex, keyInfo.KeyChar);
					cursorIndex++;
					RefreshCandidates();
					Render();
				}

				break;
			}
		}

		// Suggestions only make sense for text typed up to the true end
		// of the line - once the caret has moved left into the middle,
		// there's no single well-defined "completion" to offer.
		void RefreshCandidates() {
			candidates = buffer.Length == 0 || cursorIndex != buffer.Length
				? []
				: getCompletions(buffer.ToString());

			candidateIndex = 0;
		}

		// Accepts the current suggestion up to (and including) its next
		// space, then immediately reloads candidates for the new, longer
		// buffer - so pressing Tab/Right repeatedly steps through a
		// multi-word suggestion one word at a time. Falls through to the
		// rest of the candidate when there's no further space to stop
		// at, so a single-word suggestion is still accepted whole.
		void AcceptPartial() {
			var accepted = AcceptUpToNextSpace(
				buffer.ToString(),
				candidates[candidateIndex]);

			buffer.Clear();
			buffer.Append(accepted);
			cursorIndex = buffer.Length;
			RefreshCandidates();
			Render();
		}

		// `candidate` always starts with `typed` (every candidate is a
		// full replacement for what's been typed so far). Scans forward
		// from there for the next space and returns everything up to and
		// including it; a space inside a "quoted" region doesn't count -
		// scanning the already-typed portion first establishes whether
		// we're already inside an open quote before looking for a
		// stopping point.
		static string AcceptUpToNextSpace(string typed, string candidate) {
			var insideQuotes = false;

			for (var i = 0; i < typed.Length; i++) {
				if (typed[i] == '"') {
					insideQuotes = !insideQuotes;
				}
			}

			for (var i = typed.Length; i < candidate.Length; i++) {
				if (candidate[i] == '"') {
					insideQuotes = !insideQuotes;
				} else if (candidate[i] == ' ' && !insideQuotes) {
					return candidate[..(i + 1)];
				}
			}

			return candidate;
		}

		// Moves the terminal cursor left by `count` characters, wrapping
		// across rows using BufferWidth. Anchored to the LIVE current
		// cursor position (read fresh every call), not a remembered one -
		// that's what makes this safe across an auto-scroll, unlike
		// Console.SetCursorPosition against a stale saved coordinate.
		static void MoveCursorLeftBy(int count) {
			if (count <= 0) {
				return;
			}

			var width = Math.Max(1, Console.BufferWidth);
			var absolute = (long)Console.CursorTop * width + Console.CursorLeft - count;

			if (absolute < 0) {
				absolute = 0;
			}

			var maxTop = Math.Max(0, Console.BufferHeight - 1);
			var top = (int)Math.Min(absolute / width, maxTop);
			var left = (int)(absolute % width);

			Console.SetCursorPosition(left, top);
		}

		void Render() {
			// Return to the true start of the line before redrawing -
			// moving back exactly as far as the caret currently sits
			// on-screen (set at the end of the previous Render/FinishLine).
			MoveCursorLeftBy(caretOffset);

			var typed = buffer.ToString();

			// Only non-empty when cursorIndex == typed.Length, since
			// RefreshCandidates clears candidates otherwise.
			var ghost =
				candidates.Count > 0
				&& candidates[candidateIndex].Length > typed.Length
					? candidates[candidateIndex][typed.Length..]
					: "";

			Console.Write(typed);

			if (ghost.Length > 0) {
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write(ghost);
				Console.ResetColor();
			}

			var printedLength = typed.Length + ghost.Length;
			var trailingSpaces = 0;

			// Erase whatever leftover characters the previous, longer
			// render left behind (e.g. after Backspace or a lost match).
			if (printedLength < lastRenderedLength) {
				trailingSpaces = lastRenderedLength - printedLength;
				Console.Write(new string(' ', trailingSpaces));
			}

			lastRenderedLength = printedLength;

			// Move back from "end of everything just printed" to the
			// caret's logical position (cursorIndex characters from the
			// true start of the line).
			MoveCursorLeftBy(printedLength + trailingSpaces - cursorIndex);
			caretOffset = cursorIndex;
		}

		void FinishLine() {
			// Redraw the full line with no ghost text so the accepted
			// line is what's left on screen, then move past it for the
			// response below.
			MoveCursorLeftBy(caretOffset);

			var line = buffer.ToString();
			Console.Write(line);

			if (lastRenderedLength > line.Length) {
				Console.Write(new string(' ', lastRenderedLength - line.Length));
			}

			Console.WriteLine();
		}
	}
}
