# DICOM to STL Converter

A Unity project converts DICOM medical images into 3D printable STL files.

## Features

*   **DICOM Import:** Load and Visualize DICOM series.
*   **Cutout Box:** Provides multiple cutout boxes for mesh and voxel clipping with watertight clipping option.
*   **STL Export:** Export the generated mesh to an STL file in binary or ASCII format with finer or cross-section box option.
*   **Surface Deviation (GPU):** Computes and visualizes the geometric error between an STL model and the source DICOM data.

## Getting Started

These instructions will get you a copy of the project up and running on your local machine for development and testing purposes.

### Prerequisites

*   Unity 6000.0.33f1 or later

### Installation

1.  Clone the repository:
    ```sh
    git clone https://github.com/boyonglin/STL-Converter.git
    ```
2.  Copy the `Assets` folder into your Unity project.
3.  Install `EasyVolumeRendering` (Unity Asset Store) and `pb_Stl` (Git URL) packages from Unity Package Manager.

## Usage

### DICOM to STL Conversion

1.  Load a DICOM series.
2.  Select a GameObject with the `VolumeRenderedObject` in the Hierarchy or Scene.
3.  Adjust the visible value range to define the desired surface.
4.  From the top menu, go to `Tools/Mesh Preview Editor` and click `Export STL to Binary` or `ASCII`.
5.  The exported STL file will appear in the `Assets` folder.

### Surface Deviation Analysis

This feature allows you to compare an STL model against the original DICOM volume to assess its accuracy.

1.  Ensure you have both the DICOM volume (with a `VolumeRenderedObject`) and the STL model loaded in your scene.
2.  Create a GameObject and add the `StlErrorPainterGPU` component to it.
3.  In the Inspector for `StlErrorPainterGPU`:
    *   Drag the GameObject containing the `VolumeRenderedObject` to the `Dicom Root` field.
    *   Drag the parent GameObject of your imported STL mesh(es) to the `Stl Root` field.
4.  Adjust the parameters under `Probe settings`, `Color mapping`, and `Stats` as needed.
5.  Click the **GPU Bake Colors** button at the top of the component in Play Mode.
6.  The STL mesh vertices will be colored to show the deviation from the DICOM surface, and detailed statistics will be logged to the console.

## Built With

*   [EasyVolumeRendering](https://github.com/mlavik1/UnityVolumeRendering) - For volume rendering of DICOM data.
*   [MarchingCubes](https://github.com/Scrawk/Marching-Cubes) - For implementing the Marching Cubes algorithm.
*   [Unity-STL](https://github.com/WorldOfZero/Unity-STL) - For mesh preview editor window.
*   [pb_Stl](https://github.com/karl-/pb_Stl) - For importing STL files.

## Acknowledgments

*   Thanks to the creators of the open-source libraries used in this project.

## License

This project is licensed under the MIT License.
