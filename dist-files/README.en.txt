TidyDesktop {VERSION}
====================
Made by 404 Architect  ·  https://www.404architect.com

TidyDesktop keeps your desktop icons tidy in "boxes".
It works with the real desktop icons, so opening an icon with a double-click,
its right-click menu, and drag and drop all work as usual.


1. Running it
-------------
Put TidyDesktop.exe in any folder (Documents, for example) and run it. There is nothing to install.

* If "Windows protected your PC" appears, click "More info", then "Run anyway".
  The program is not signed with a Microsoft-trusted certificate, which is why Windows warns.
  Your antivirus may also ask you to confirm.

* Once running, TidyDesktop has an icon at the bottom right of the taskbar, next to the clock.
  Right-click it for every menu. If you cannot see it, click the ^ button next to the clock.


2. What changes on the first run
--------------------------------
- The desktop's "Auto arrange icons" and "Align icons to grid" are turned off,
  so icons can sit freely inside boxes.
- A "Tidy box" is added.
- Your icon positions at that moment are saved, so you can always go back.


3. Using it
-----------
- Tray menu > "Sort into boxes by kind": icons are sorted into Web, Office, Games, Pictures, Documents … boxes.
- Drag an icon into a box to add it; drag it out to take it out.
- Drag a box's title to move the box. Drag its left, right or bottom edge to resize it.
  Hold Shift while dragging to snap to a grid.
- Double-click a box's title to roll it up or unroll it.
- Right-click a box's title for: Customize (name, color, opacity), Rename, Sort, Lock position, Delete, and more.
- Recent changes box: shows files added to, removed from or renamed on the desktop.
  For a file that left the desktop it tells you where it went, or that it went to the Recycle Bin.
  Click an entry to open the file if it is on the desktop, or its folder if it went elsewhere.
- Tray menu > "File rules…": make rules such as "move mp4 files to the Videos folder".
  A file a rule moved can be put back with "Undo" in the Recent changes box.
  * Rules move real files, so set the conditions carefully.
- Tray menu > "Start with Windows": TidyDesktop starts every time you turn on the computer.
- Tray menu > "Language / 언어": choose English, Korean, Japanese, Chinese, Spanish, German, French, Portuguese, or Russian (default: follow Windows).


4. Going back
-------------
- Changed the layout by mistake: Tray menu > "Go back to a saved layout", then pick a time.
  (Layouts are saved every 30 minutes, and right before every big change.)
- Back to how things were before TidyDesktop: Tray menu > "Remove all boxes and restore icon positions".


5. Removing it
--------------
1) Tray menu > "Remove all boxes and restore icon positions"
2) Tray menu > "Start with Windows": turn it off if it is on
3) Tray menu > "Exit"
4) Delete TidyDesktop.exe
5) To remove the settings too: Windows key + R > type %APPDATA%\TidyDesktop > delete the folder that opens


6. Good to know
---------------
- Settings and history are stored only in %APPDATA%\TidyDesktop and are never sent anywhere.
- To tell you where a file that left the desktop went, TidyDesktop watches for files being
  created or renamed on this computer's drives. It never reads file contents, and only records
  a file whose name matches one that just left the desktop.
- If you send log.txt to someone to report a problem, note that it contains desktop file names.
- For Windows 10 and 11.
- To add a language, copy en.json from the source code, translate it, set "_code" and "_name",
  and put it in a "Languages" folder next to TidyDesktop.exe.


7. Made by
----------
404 Architect  ·  https://www.404architect.com
Click "Made by" at the top of the tray menu to open the site.
