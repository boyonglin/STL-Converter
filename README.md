# DICOM to STL Converter

A Unity project converts DICOM medical images into 3D printable STL files.

## Features

*   **DICOM Import/Volume Rendering:** Load and Visualize DICOM series.
*   **Isosurface Extraction:** Generate a 3D mesh from the volume data using the Marching Cubes algorithm.
*   **STL Export:** Export the generated mesh to an STL file in binary or ASCII format.
*   **STL Import:** Import models with vertex counts larger than Unity max by automatically splitting into multiple meshes.

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
3.  Install `EasyVolumeRendering` and `pb_Stl` packages from Unity Package Manager.

## Usage

1. Select a the UI to select a DICOM serie1s
4.  Adjust the isosurface threshold.
5.  Click "Export to STL" to save the generated model.

## Built With

*   [EasyVolumeRendering](https://github.com/mlavik1/UnityVolumeRendering) - For volume rendering of DICOM data.
*   [MarchingCubes](https://github.com/Scrawk/Marching-Cubes) - For implementing the Marching Cubes algorithm.
*   [Unity-STL](https://github.com/WorldOfZero/Unity-STL) - For importing STL files.
*   [pb_Stl](https://github.com/karl-/pb_Stl) - For mesh preview editor window.

## Acknowledgments

*   Thanks to the creators of the open-source libraries used in this project.

## License

This project is licensed under the MIT License.
