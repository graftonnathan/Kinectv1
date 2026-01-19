# .NET 8.0 Upgrade Report

## Project target framework modifications

| Project name     | Old Target Framework | New Target Framework | Commits                          |
|:-----------------|:--------------------:|:--------------------:|:---------------------------------|
| Kinectv1.csproj  | net481               | net8.0-windows       | 07f4a895, f1e1097a, 2e247b73     |

## NuGet Packages

| Package Name                            | Old Version      | New Version | Commit Id    |
|:----------------------------------------|:----------------:|:-----------:|:-------------|
| Microsoft.Kinect                        | 2.0.1410.19000   | (removed)   | f1e1097a     |
| Microsoft.Kinect.Face.x64               | 2.0.1410.19000   | (removed)   | f1e1097a     |
| Newtonsoft.Json                         | 13.0.3           | 13.0.4      | f1e1097a     |
| System.Configuration.ConfigurationManager | -              | 10.0.2      | f1e1097a     |
| System.Threading.Channels               | 7.0.0            | 8.0.0       | f1e1097a     |

## Assembly References Removed

The following .NET Framework assembly references were removed (replaced by SDK implicit references or NuGet packages):
- System.ComponentModel.DataAnnotations
- System.Configuration
- System.Net.Http
- System.Windows.Forms

## All commits

| Commit ID  | Description                                                           |
|:-----------|:----------------------------------------------------------------------|
| 5e038811   | Commit upgrade plan                                                   |
| 07f4a895   | Update Kinectv1.csproj to net8.0-windows and refactor packages        |
| f1e1097a   | Upgrade project to .NET 8 and update dependencies                     |
| 2e247b73   | Removed Kinect runtime references and updated build messages for .NET 8.0 |

## Project feature upgrades

### Kinectv1.csproj

Here is what changed for the project during upgrade:

- Target framework upgraded from `net481` to `net8.0-windows`
- WPF support configured via `UseWPF` property in SDK-style project
- Kinect SDK packages (Microsoft.Kinect, Microsoft.Kinect.Face.x64) removed as they are incompatible with .NET 8
- NuGet packages updated to .NET 8 compatible versions
- Legacy .NET Framework assembly references replaced with modern SDK references and NuGet packages
- Build verification messages updated to reflect .NET 8.0
- Removed Kinect runtime copy targets from the build process

## Next steps

- Review and test the application to ensure all functionality works correctly without Kinect
- Consider removing any remaining Kinect-related code files if they still exist
- Run the application to verify WPF functionality on .NET 8.0
- Update any CI/CD pipelines to use .NET 8.0 SDK
