using System;
using System.Globalization;

namespace Kinectv1.UI.Settings.Validation
{
    /// <summary>
    /// Validates that a value is not null or empty
    /// </summary>
    public class RequiredValidator : IValidator
    {
        public string ErrorMessage { get; set; } = "This field is required";
        public ValidationSeverity Severity { get; set; } = ValidationSeverity.Critical;

        public ValidationResult Validate(object value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
            {
                return ValidationResult.Error(ErrorMessage, Severity);
            }
            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Validates that a numeric value is within a specified range
    /// </summary>
    public class RangeValidator : IValidator
    {
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
        public string ErrorMessage { get; set; }
        public ValidationSeverity Severity { get; set; } = ValidationSeverity.Critical;

        public RangeValidator(double minValue, double maxValue)
        {
            MinValue = minValue;
            MaxValue = maxValue;
            ErrorMessage = $"Value must be between {minValue} and {maxValue}";
        }

        public ValidationResult Validate(object value)
        {
            if (value == null)
                return ValidationResult.Error("Value cannot be null", Severity);

            if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double numValue))
            {
                return ValidationResult.Error("Value must be a valid number", Severity);
            }

            if (numValue < MinValue || numValue > MaxValue)
            {
                return ValidationResult.Error(ErrorMessage, Severity);
            }

            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Validates that a string has a minimum length
    /// </summary>
    public class MinLengthValidator : IValidator
    {
        public int MinLength { get; set; }
        public string ErrorMessage { get; set; }
        public ValidationSeverity Severity { get; set; } = ValidationSeverity.Critical;

        public MinLengthValidator(int minLength)
        {
            MinLength = minLength;
            ErrorMessage = $"Minimum length is {minLength} characters";
        }

        public ValidationResult Validate(object value)
        {
            var str = value?.ToString() ?? string.Empty;
            if (str.Length < MinLength)
            {
                return ValidationResult.Error(ErrorMessage, Severity);
            }
            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Validates that a string matches a pattern
    /// </summary>
    public class PatternValidator : IValidator
    {
        public string Pattern { get; set; }
        public string ErrorMessage { get; set; }
        public ValidationSeverity Severity { get; set; } = ValidationSeverity.Critical;

        public PatternValidator(string pattern, string errorMessage)
        {
            Pattern = pattern;
            ErrorMessage = errorMessage;
        }

        public ValidationResult Validate(object value)
        {
            var str = value?.ToString() ?? string.Empty;
            if (!System.Text.RegularExpressions.Regex.IsMatch(str, Pattern))
            {
                return ValidationResult.Error(ErrorMessage, Severity);
            }
            return ValidationResult.Success();
        }
    }
}
