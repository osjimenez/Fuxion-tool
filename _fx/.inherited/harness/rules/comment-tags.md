## Comment tags (TODO, PEND, INFO, IDEA, ASK…)

The user leaves notes in code comments with ReSharper to-do tags: `TODO`, `BUG`, `PEND`, `INFO`, `IDEA`, `ASK`.
To find, list or count them, use the **`comment-tags`** skill: its script reads the tag list from ReSharper's
settings and searches the root it is in (in the space, every mounted repository). The Visual Studio MCP tools do
not see comments.

- `ASK` is a question for the agent: answer it.
- `IDEA` goes to the ideas of the space (`_docs/ideas/`, its **`ideas`** skill: one file per idea, front matter,
  generated dashboard), but show the user the list with the original text first, and do not remove the comment from
  the code until the user says so.
