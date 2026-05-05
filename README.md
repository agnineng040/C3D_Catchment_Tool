# Catchment2Structure (Civil 3D add-in)

**Catchment to Structure** assigns each Civil 3D **Catchment** to a **Pipe Network Structure** when the structure’s XY location lies **inside** that catchment’s boundary. If several structures qualify, the tool picks the one **nearest** the catchment **discharge point** (with a fallback to the boundary centroid if the discharge point is not available from the API).

---

## How to use the app

### 1. Install and load

1. Build the solution (see [Build and install](#build-and-install) below), or use a prebuilt bundle.
2. Copy the entire folder `bundle\Catchment2Structure.bundle` to:
   - `%AppData%\Autodesk\ApplicationPlugins\Catchment2Structure.bundle`
3. Restart **Civil 3D** so the plug-in loads.

### 2. Open your drawing

Work in a drawing that has:

- At least one **pipe network** with **structures**, and  
- **Catchments** (or closed **polylines** on layers you plan to convert—optional).

### 3. Run the command

At the Civil 3D command line, type:

```text
C2S
```

and press Enter.

> **Note:** The Application Package metadata may list `CATCHMENT2STRUCT`; the command implemented in this project is **`C2S`**.

### 4. Use the **Catchment to Structure** window

The dialog is **modeless**: you can pan, zoom, or inspect the drawing while it stays open.

#### Run Options

| Control | Purpose |
|--------|---------|
| **Select Pipe Network** | Structure candidates come from this pipe network. The combo lists networks in the drawing; **the first entry is selected by default.** |
| **Target Catchment Group** | Only catchments in this group are processed. **The first group in the list is selected by default.** |
| **Overwrite existing assignments** | Off by default: catchments that already reference a discharge structure are **skipped**. Turn on to replace those assignments. |
| **Prompt when multiple structures found** | On by default: if **more than one** structure lies inside the boundary, the case goes to **Review** instead of auto-assigning the nearest to the discharge point. |
| **If none inside catchment, prompt to select structure** | On by default: when **no** structure is inside the boundary, you can resolve the catchment in **Review** by picking a structure; otherwise it counts as “no structure found.” |

Click **Run** when ready. The add-in runs the actual work as a follow-on command so Civil 3D can show **entity picks** at the command line when needed.

#### Optional: Polyline → Catchment conversion

Use this when boundaries exist as **closed LWPolylines** instead of Civil catchments.

1. Enable **Convert closed polylines to catchments before assignment**.
2. Under **Layers to Convert**, check the layers that contain those polylines (**All** / **None** help bulk-select).
3. Set **Catchment Style**, **Reference Surface**, and **Create in Catchment Group** (or type a **New Group Name** when creating a new group—see the combo behavior in the UI).
4. Options:
   - **Erase polylines after conversion** — removes source polylines after a successful conversion.
   - **Only process catchments created in this run** — after conversion, assignment runs **only** on new catchments from this session (default **on**).

Polylines must be **closed** and **non-self-intersecting**. Problem polylines are listed in a separate **issues** window after the run if conversion fails for some entities.

### 5. During the run

- **Automatic assignment:** For each catchment, structures whose XY position is inside the 2D boundary are candidates. The nearest candidate to the discharge point (or centroid) gets the assignment unless rules send the case to review.
- **Review window:** Appears when you chose to prompt for **multiple structures** or **no structure inside**. You can **Go to** geometry, **Assign** (pick a structure in the drawing), or **Skip**. Finish when done; the tool loops until you complete the review flow.

### 6. Results

- A summary is written to the **command line**.
- A **Results** dialog shows the same summary (counts for assigned, skipped, no structure, plus option echo).
- If polyline conversion had issues, a **Polyline issues** window may open (modeless) with handles and layers.

---

## Behavior details (reference)

- **Inside/outside:** Structure insertion point is tested against the catchment **boundary polyline** (including on-edge tolerance).
- **Multiple structures:** With prompting enabled, you resolve ambiguity in the Review window or by picking at the command line when requested.
- **Performance:** Very large boundaries may skip some expensive self-intersection checks during polyline validation (see code constants).

---

## Build and install

### Prerequisites

- Visual Studio 2022  
- **.NET 8 SDK** (for Civil 3D 2025+)  
- Civil 3D installed (for reference assemblies)

### Civil 3D install paths

Edit **`Directory.Build.props`** at the solution root:

- `Civil3DNet8Root` — AutoCAD 2025/2026 base folder (contains `AcDbMgd.dll`)  
- `Civil3DNet48Root` — AutoCAD 2024 base folder (if used by the solution)

Civil 3D managed DLLs are expected under the `C3D` subfolder (e.g. `...\C3D\AeccDbMgd.dll`).

### Build

Open `Catchment2Structure.sln` in Visual Studio and build the **Catchment2Structure.Net8** project.

Output **Autoloader bundle**:

- `bundle\Catchment2Structure.bundle`

Install by copying that folder to:

- `%AppData%\Autodesk\ApplicationPlugins\Catchment2Structure.bundle`

Restart Civil 3D.

### Debug

In Visual Studio: **Project Properties → Debug → Start external program** → point to your Civil 3D `acad.exe` (same folder as `AcDbMgd.dll`). Optional arguments: `/nologo`. Press **F5**.

---

## Package metadata

- **ProductCode:** `24F3E486-C396-4785-BB94-7CD1D36D8313`  
- **Preconfigured install root (example):** `C:\Program Files\Autodesk\AutoCAD 2026\`  
- **RuntimeRequirements** in `PackageContents.xml` target Civil 3D **2025–2026** (`SeriesMin` / `SeriesMax`). Bump `SeriesMax` when supporting newer Civil 3D versions and rebuild/test.

---

## Solution layout

| Item | Description |
|------|-------------|
| `src/Catchment2Structure.Net8` | .NET 8 plug-in: commands `C2S`, `C2S_RUN`, WPF UI |
| `bundle/Catchment2Structure.bundle` | Autodesk Application Plug-in bundle output |

Visual Studio: **.NET Desktop Development** workload; VS 2022 **17.8+** recommended for .NET 8.
