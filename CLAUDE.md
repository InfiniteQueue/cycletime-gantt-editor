# CellGanttChartEditor2

A WPF (.NET 9) editor for robot cell cycle charts: a reference image with regions marked on it, and
a looping Gantt chart of the operations carried out in them.

## UI conventions

### The crosshair means "point at something on the image"

Any control that sends the user to the reference image to pick a point or draw an area shows the
crosshair icon and **no text**. It is the one symbol for that action, and it is used consistently:

- the mark drawn on the image for a robot's place;
- the button beside the Region field in the add-operation dialog (draw a box, or click an existing
  region);
- the button beside the Robot field in the same dialog (place the robot);
- the buttons in the items panel for a region's area and a robot's place.

**All code that returns or draws this icon lives in `Services/CrosshairIcon.cs`.** Do not redraw the
shape anywhere else - take the geometry or the drawing helper from that class. The button styling
that wraps it is the `PickButton` style in `Themes/Dark.xaml`, which is the only place the icon is
turned into a control.

The reason for the button-with-no-text is that these buttons sit in field rows where a label already
says what the field is; the icon says what the button does to it.

### Picking closes the dialog

A modal dialog cannot stay up while the user works on the image behind it. The add-operation dialog
therefore closes when a crosshair button is pressed, the host runs the pick, and the dialog reopens
on the same `OperationDraft` with the answer filled in. Anything half-typed has to survive that trip,
which is why the draft holds the numeric fields as text.

### The filter chips are whatever the bars are coloured by

Picking a chip means "keep the work wearing this colour", so the set of chips is always
`ChartView.EffectiveColorBy` - regions while the bars are coloured by region, robots while they are
coloured by robot. The chart keeps a set for each (`RegionFilter`, `RobotFilter`) but only ever
consults the one in use; **ask `Filtering` and `InFilter`, not either set's count.**

Changing the colour source clears both, in `SetColorMode` and `SetGroupMode`, since what the user
picked names something that is no longer on offer. The catch-all rows are not chips, for the same
reason they are not items - there is nothing to select that would mean "the ones with none".

## Model rules

- **Robots are named, not owned.** A robot exists while an operation names it. A `RobotInfo` record
  is its registration: created when the user adds a robot or places one on the image, and removed
  only by deleting the robot outright. A robot that was never registered goes when its last
  operation does.
- **An operation needs neither a robot nor a region.** `Operation.RegionId` is `Guid.Empty` for none
  and `RobotName` is empty for none - test with `Operation.HasRegion` and `Operation.HasRobot`. Each
  kind gathers in a catch-all row - "(No region)" or "(No robot)" - when the chart's rows are the
  thing it lacks, and takes a neutral fill when the colouring is keyed on it. Neither catch-all is an
  item: they cannot be renamed, and two operations that both lack the same thing do not conflict over
  it. Only the name and the timing are required.
- **A region need not have an area.** `ChartRegion.Bounds` may be empty - test with
  `ChartRegion.HasArea`. Such a region is not drawn on the image but is a region in every other way.
- **Regions, robots and operations are added and deleted deliberately**, through the items panel.
  Nothing is pruned automatically because it became empty; deleting a region or a robot takes the
  operations defined against it (and their links) with it.
- **An operation is renamed from the items panel or by double-clicking it.** Double-clicking a bar
  opens `BarEditDialog`, which is where the name lives alongside the timing; double-clicking a row
  header renames the row, which in the chronological view is the operation itself.
- Start times are absolute and are never rewritten when the cycle time changes. Only the drawing
  wraps.

## Undo and redo

Ctrl+Z (and ctrl+Y, or ctrl+shift+Z, to come back) is built on snapshots, not on a list of commands. `MainWindow.RefreshAll` is where every edit
in the app ends up - the chart raises `Edited`, the items panel raises `Edited`, the dialogs return to
a host method - so it takes a `DocumentSnapshot` there, and when the new one differs from the one
kept from last time, that older one becomes a place ctrl+z can go back to.

The consequence for new code: **an edit only needs to end in `RefreshAll` to be undoable.** Nothing
has to announce itself beforehand, and a refresh that changed nothing (a save, a new image) does not
put a dead step on the stack. Undo hands the state it left to the redo stack; a genuine new change
clears it, since what was undone is no longer ahead of the user.

A snapshot holds model objects by reference with their fields copied beside them, so restoring puts
the very same `Operation` back and a chart still pointing at it stays valid. Row layouts are
deliberately outside it: dragging a row or collapsing a group arranges the view rather than changing
the chart, and neither should be undoable.

## Drawing

`ChartView` and `ImageRegionView` render themselves in `OnRender` with a `DrawingContext` and do
their own hit testing - there are no WPF elements per bar, row or region. Two consequences:

- Guard against degenerate sizes. A `Rect` with a negative width throws, and thrown from `OnRender`
  it comes out on the WM_SIZE path where nothing catches it and the app dies.
- Colours live in `Services/Palette.cs` for the drawn surfaces and `Themes/Dark.xaml` for the XAML
  controls. Keep the two in step.
- **Fills come from `ColorAllocator`, which picks them in CIELCh** - nine hues spaced evenly round
  that circle, each taken at the lightness where sRGB lets it be most colourful, then moved clear of
  half luminance. Equal steps in CIELCh are meant to be equal steps in *perceived* hue; equal steps
  in HSV are not, so do not reach for HSV here.
- **Text on a coloured surface asks `ColorAllocator` which of black and white to use** -
  `GetTextBrush` for an allocated fill, `TextOn` for any other colour. One rule decides it for the
  filter chips and the bars alike. A bar is not all one colour, though: a clash paints part of it
  white, so its label is drawn twice, each pass clipped to the parts that colour belongs on.

WPF keys implicit styles on an element's exact runtime type, so a bare `TargetType="Window"` style
never reaches a subclass. Every window in this app asks for `ThemedWindow` by key.
