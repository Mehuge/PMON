General Rules
-------------

- Always use early return patterns
- Always always use early return patterns
- Never ever use else
- No slopp
- Use Avalonia UI unless told otherwise
- Make pretty UIs from the outset
- Use net10.0 unless there is good reason not to
- Add launch and debug configs for visual studio code
- Release builds should be standalone executables unless specified otherwise
- Don't put everything in src (are you mad) only source code (e.g. .cs and .axaml files) goes in src, everything else goes in the parent folder.
- Generate Icon and Logo PNGs
- Include an about dialog showing the program version, author (Mehuge) and you can mention the AI model name used.
- Add a package.json with run scripts for building debug/release builds and launching
- Test your changes by building and checking for errors and fixing them

This Project
------------

This project is a tool for monitoring one or more processes (identified by a list of patterns) and both visually displaying and back ground recording to a file (csv or some other suitable format) the processes resource usage and activity.

timestamp
cpu usage
thread count
memory usage (in its various forms)
paging and swap usage
state (foreground, background, window position etc)
and any other metrics we can gather about a process that may be useful in tracking its activity.

Should also record overall basic metrics such as cpu usage load and memory usage /load and disk activity.

The purpose of this app is to monitor suspect processes activity so when a slowdown occurs or some other unwanted event happens, I can look back at the log and see what this/these processes were doing.

The UI should be modern, pretty, should include graphs (perhaps tabbed and grouped around metric type).

A recording interface.

A recording should be able to be loaded and displayed in the graphs, with a timeline selector (perhaps a wide verticaly thin rendering of overall cpu/memory) and a cursor / thumb that can be dragged along left/right to move the currently displayed samples to that time index.
