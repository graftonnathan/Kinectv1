# .NET 8.0 Upgrade Plan

## Execution Steps

Execute steps below sequentially one by one in the order they are listed.

1. Validate that a .NET 8.0 SDK required for this upgrade is installed on the machine and if not, help to get it installed.
2. Ensure that the SDK version specified in global.json files is compatible with the .NET 8.0 upgrade.
3. Upgrade Kinectv1.csproj to .NET 8.0

## Settings

This section contains settings and data used by execution steps.

### Excluded projects

Table below contains projects that do belong to the dependency graph for selected projects and should not be included in the upgrade.

| Project name                                   | Description                 |
|:-----------------------------------------------|:---------------------------:|
| (none)                                         |                             |

### Aggregate NuGet packages modifications across all projects

NuGet packages used across all selected projects or their dependencies that need version update in projects that reference them.

| Package Name                   | Current Version    | New Version | Description                                                        |
|:-------------------------------|:------------------:|:-----------:|:-------------------------------------------------------------------|
| Microsoft.Kinect               | 2.0.1410.19000     |             | Remove - Kinect functionality being removed                        |
| Microsoft.Kinect.Face.x64      | 2.0.1410.19000     |             | Remove - Kinect functionality being removed                        |
| Newtonsoft.Json                | 13.0.3             | 13.0.4      | Recommended for .NET 8.0                                           |
| System.Threading.Channels      | 7.0.0              | 8.0.0       | Recommended for .NET 8.0                                           |

### Project upgrade details

This section contains details about each project upgrade and modifications that need to be done in the project.

#### Kinectv1.csproj modifications

Project properties changes:
  - Target framework should be changed from `net481` to `net8.0-windows`

NuGet packages changes:
  - Microsoft.Kinect (2.0.1410.19000) - **REMOVE**: Package and all related Kinect functionality will be removed
  - Microsoft.Kinect.Face.x64 (2.0.1410.19000) - **REMOVE**: Package and all related Kinect Face functionality will be removed
  - Newtonsoft.Json should be updated from `13.0.3` to `13.0.4` (*recommended for .NET 8.0*)
  - System.Threading.Channels should be updated from `7.0.0` to `8.0.0` (*recommended for .NET 8.0*)

Other changes:
  - WPF project: Ensure `UseWPF` property is set to `true` in the SDK-style project file
  - Windows-specific: Target framework should use `-windows` suffix for WPF support
  - Remove all Kinect-related code files and references from the project
