# WWR UV Exporter

A Unity editor extension that exports a mesh's UV layout as a PNG. It supports material preview, UDIM tile ranges, UV channel / submesh selection, and wireframe / triangle-fill rendering.

## Features

- Live UV layout preview with optional material preview
- UDIM tile range support (auto-detect or manual)
- UV channel (UV0–UV7) and submesh selection
- Wireframe, triangle fill, and background options with per-element colors
- Export to PNG up to 8192×8192, with transparency support

## Requirements

- Unity 2022.3 or later

## Installation

### Via VCC / ALCOM (recommended)

1. Add the WWR VPM listing to VCC:
   `https://wwr-alliance.github.io/vpm-listing/index.json`
   (or click **Add to VCC**: `vcc://vpm/addRepo?url=https://wwr-alliance.github.io/vpm-listing/index.json`)
2. Open your project in VCC and add **WWR UV Exporter** from the package list.

### Manual

Download the latest `.unitypackage` from [Releases](../../releases) and import it into your project.

## Usage

Open the window from **Tools > WWR > UV Exporter**.

1. Select a GameObject (SkinnedMeshRenderer / MeshFilter) from the scene, or a Mesh asset.
2. Adjust the UV channel, submesh, tile range, and draw settings.
3. Click **Export PNG** to save the image.

## License

MIT License. This tool was initially inspired by [Dolphiiiin/UVExporter](https://github.com/Dolphiiiin/UVExporter) (MIT). See [LICENSE](Packages/com.wwr3d.uv-exporter/LICENSE) for details.
