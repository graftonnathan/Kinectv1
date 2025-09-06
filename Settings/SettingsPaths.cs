// Settings/SettingsPaths.cs
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Kinectv1.Settings
{
    public static class SettingsPaths
    {
        private static Assembly GetCompanyProductAssembly() =>
            Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly() ?? typeof(SettingsPaths).Assembly;

        public static string Company
        {
            get
            {
                var asm = GetCompanyProductAssembly();
                var company = asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
                if (string.IsNullOrWhiteSpace(company))
                    throw new InvalidOperationException("Missing AssemblyCompanyAttribute");
                return company;
            }
        }

        public static string Product
        {
            get
            {
                var asm = GetCompanyProductAssembly();
                var product = asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
                if (string.IsNullOrWhiteSpace(product))
                    throw new InvalidOperationException("Missing AssemblyProductAttribute");
                return product;
            }
        }

        public static string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Company, Product);

        public static string ProgramDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Company, Product);

        public static string UserJsonPath => Path.Combine(AppDataDir, "settings.json");
        public static string SecretsPath => Path.Combine(AppDataDir, "secrets.json");
        public static string MachineDefaultsPath => Path.Combine(ProgramDataDir, "defaults.json");

        // Deterministic: {AssemblyName}.Settings.default.json
        public static string DefaultResourceName
        {
            get
            {
                var asm = typeof(SettingsPaths).Assembly;
                var name = asm.GetName().Name + ".Settings.default.json";
                var found = asm.GetManifestResourceNames().FirstOrDefault(n => string.Equals(n, name, StringComparison.Ordinal));
                if (found == null)
                    throw new FileNotFoundException("Embedded default.json not found. Ensure Build Action = EmbeddedResource and name '" + name + "'.");
                return found;
            }
        }
    }
}
