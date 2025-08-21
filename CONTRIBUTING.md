# Contributing

## Native DLLs during development
- Place any native DLLs you need for local testing under the `lib/` folder in the repo root (e.g., CUDA/cuDNN/TensorRT, Discord voice libs).
- Do not commit random DLLs into the project root or `bin/`.

## How the build organizes native dependencies
- The build creates a categorized `lib/` structure in the repo as needed:
  - `lib/onnxruntime/`   ONNX Runtime core and providers
  - `lib/cuda/`          CUDA toolkit DLLs
  - `lib/cudnn/`         cuDNN runtime DLLs
  - `lib/tensorrt/`      TensorRT runtime and parsers
  - `lib/discord/`       Discord voice native dependencies
  - Additional folders like `lib/vosk/`, `lib/kinect/` may be created for NuGet runtimes
- Any DLLs first placed in `lib/` are copied into the appropriate `lib/<category>` folder, and duplicates in `lib/` are removed by the build.
- NuGet native assets from `runtimes/**` are copied into `bin/lib/<category>` automatically.
- The final app output is normalized to `bin/lib/**` so the runtime can resolve dependencies reliably.

## Model files and prompts
- All content under `models/**` is copied to `bin/models/**` at build.
- Prompt files under `prompts/**` are copied to `bin/prompts/**` at build.

## Package version management
- Package versions are centrally pinned in `Directory.Packages.props`.
- Do not add `Version="..."` on individual `PackageReference` entries in project files.

## General guidelines
- Keep edits minimal and localized. Avoid refactors unless requested.
- Do not add secrets or licensed binaries.
- Test a full rebuild after changing build targets or native dependency layout.
