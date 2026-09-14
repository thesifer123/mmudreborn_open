# Runtime art assets

This directory is intentionally empty in the published repository.

The engine reads optional ANSI art and sign text from here at runtime — the area
maps rendered by the `map` command, and the custom sign text shown in a few town
rooms. Those files are **game content, not engine code**, and are not distributed
here. Supply them yourself from a legally obtained copy of the original game data.

Drop the files in this directory (they are copied next to the built binary, and
`*.ANS` files are also embedded in the assembly as a deploy fallback). With the
directory empty the server still builds and runs; `map` simply has nothing to draw.
