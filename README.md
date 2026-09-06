# Cycle Time Gantt Editor

A Windows desktop editor for **robot cell cycle charts**: a layout drawing of the cell with its
stations marked on it, and a looping Gantt chart of the operations carried out in them.

The key focus of this app is to make operation time overlaps/conflicts obvious not just per robot, but per station, 
and properly handle the cyclical nature of process cycles by allowing operations to wrap around to the end back to 
the start

## What it is for

A robot cell repeats the same sequence every cycle. Planning one means placing each operation in
time, keeping the robots out of each other's way, and finding the moments where the cycle is waiting
on something. Spreadsheets lose the geometry; CAD keeps the geometry and loses the time.

This keeps both. Regions drawn on the layout are the places work happens, and every bar on the chart
belongs to one, so the colour of a bar tells you where in the cell it is happening.

## Features

**The layout**

- Load any image as the cell reference — a CAD export, a screenshot, a photograph.
- Drag a box on it to define a region; regions are stored in image coordinates, so swapping the
  drawing for a newer revision leaves every region where it was.
- Place robots on the layout as crosshair markers, labelled with the robot's name.
- Wheel to zoom about the cursor, drag to pan. Region borders and markers scale with the image.

**The chart**

- A looping time axis: the chart is one cycle wide, and work running past the end is drawn wrapping
  round to the start. Start times are absolute and are never rewritten when the cycle time changes.
- Drag bars to move them, drag the trailing edge to resize. Both snap to the nearest second, or to
  another bar's end when within 0.1s — hold **Shift** to place freely.
- Link one operation to another by dragging a bar onto it. A link holds a fixed separation between
  the end of one and the start of the next, and moving the first drags everything downstream with it.
- Group rows together and collapse them; a collapsed group blocks in the occupied time of everything
  it hides.
- Rows are reorderable by dragging, and the chronological view sorts on demand rather than
  constantly rearranging itself under you.

**Clashes**

- Two operations overlapping in time on the same robot, or in the same region, are marked on both
  bars and counted in the status bar. Click the count to list the pairs.
- Operations that are *meant* to run together can be declared simultaneous, after which they never
  count as a clash — the overlap is the design, not a mistake.

**Colour**

- Bars can be coloured by region, robot, operation category or region category, and the filter chips
  above the chart always follow whatever the colours currently mean.
- Colours are allocated in CIELCh rather than HSV, chosen to maximise the smallest perceptual
  difference between any two — so nine distinct fills, then checkered pairs of them, rather than
  three shades of blue that only a colour picker can tell apart.

**Everything else**

- Ctrl+Z / Ctrl+Y across the whole application, not just the chart.
- Saves to a single `.cgc` file with the layout image embedded, so a chart is one file to send.

## Building

Needs the [.NET 9 SDK](https://dotnet.microsoft.com/download) and Windows, being a WPF application.

```bash
git clone https://github.com/InfiniteQueue/cycletime-gantt-editor.git
cd cycletime-gantt-editor
dotnet run --project CellGanttChartEditor2
```

Or open `CellGanttChartEditor2.sln` in Visual Studio and press F5.

## Mouse and keyboard

| | |
|---|---|
| **Chart** | |
| Drag a bar | Move it in time |
| Drag a bar's right edge | Change its duration |
| Hold Shift while dragging | Place freely, ignoring the snapping |
| Drag a bar onto another | Link them |
| Double click a bar / link / row header | Edit it |
| Delete | Remove the selected bar or link |
| Wheel | Zoom horizontally, about the cursor |
| Ctrl+wheel, or wheel over the row headers | Zoom vertically — taller or shorter rows |
| Alt+wheel / Shift+wheel | Scroll across / down |
| Middle-drag | Pan |
| **Layout** | |
| Wheel | Zoom about the cursor |
| Drag | Pan, or draw a region when asked for one |
| Double click a region title | Rename it |
| **Anywhere** | |
| Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z | Undo, redo |
| Esc | Cancel a pick in progress |

## File format

`.cgc` is JSON with the layout image embedded as base64. The format is versioned and older files
keep loading — the current version is 5.

## Status

Written for a real cycle time study rather than as a product, and shaped by using it. It does what it
is described as doing here.
