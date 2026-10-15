---
name: comment-tags
description: Find, list or count the ReSharper to-do tags the user writes in code comments (TODO, PEND, INFO, IDEA, ASK, BUG and any other tag configured in ReSharper). Use it whenever the user mentions those tags or says they left comments, ideas or questions in the code ("he dejado IDEAS y ASK", "mira los PEND", "¿cuántos TODO quedan?"), before reviewing or refactoring an area (to see its pending notes), and when collecting ideas for `_docs/ideas/`.
---

# Comment tags (ReSharper to-do items)

The user marks comments with ReSharper to-do tags. The tag list lives in ReSharper's settings
(`%APPDATA%\JetBrains\Shared\vAny\GlobalSettingsStorage.DotSettings`, `TodoPatterns`), not in the repo. Today:
`TODO`, `BUG` (ReSharper defaults), `PEND`, `INFO`, `IDEA`, `ASK`.

The Visual Studio MCP tools cannot find them: the semantic tools work on symbols and skip comments. Use the
script in this folder, which reads the tags from the ReSharper settings and matches them like ReSharper does
(`(?<=\W|^)TAG(\W|$)`, only inside comments).

## Conventions in the comments

- A tag starts an entry: `// IDEA - text…`. An entry can continue on the following comment lines
  (`// - point`, `//    - sub-point`) and may be closed with `// FIN IDEA` (or `FIN ASK`…).
- `ASK` = a question for the agent: answer it in the conversation.
- `IDEA` = something to keep. Before moving it to `_docs/ideas/` (a new `I-NN` file, see the `ideas` skill), show the user the list with the
  **original text** and wait; do not remove the comment from the code until the user says so.
- `PEND`/`TODO` = pending work in that spot; `INFO` = an explanation worth keeping.

## Script

Run from anywhere (paths are resolved from the script location; the default search root is the root the skill is in:
in the workspace, every mounted repo; in a repository, itself; `bin`, `obj`, `.git`, `.vs`, `TestResults` and
`archive` are skipped):

```bash
perl .claude/skills/comment-tags/find-tags.pl --list-tags          # tags in use and where they come from
perl .claude/skills/comment-tags/find-tags.pl --count              # how many of each
perl .claude/skills/comment-tags/find-tags.pl --tags IDEA,ASK --block   # entries with their whole block
perl .claude/skills/comment-tags/find-tags.pl --tags PEND oss/repo/solution/src/AspNetCore   # one tag, one folder
```

Options: `--tags A,B` (case-sensitive, like ReSharper), `--count`, `--block` (prints the following comment lines
up to `FIN <TAG>` or the first non-comment line), `--all` (include `archive`), and paths (files or folders).
Output is `path:line: comment`, paths relative to that root.

## Finding what the user just wrote

To know which tagged comments are new (for example, "he dejado comentarios en los ficheros abiertos"), compare
against a working-tree snapshot instead of trusting `git diff` (untracked files would look deleted):
take a snapshot tree with `git write-tree` over a temporary `GIT_INDEX_FILE`, then `git diff --stat <old> <new>`
between two snapshots. The Visual Studio MCP tools only expose the active selection, not the list of open documents.
