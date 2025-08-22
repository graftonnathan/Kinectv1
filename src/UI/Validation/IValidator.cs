using System;

namespace Kinectv1.src.UI.Validation
{
    /// <summary>
    /// Represents a validation rule with severity level
    /// </summary>
    public enum ValidationSeverity
    {
        Warning,  // Allow save with confirmation
        Critical  // Block save operation
    }

    /// <summary>
    /// Base interface for all validators
    /// </summary>
    public interface IValidator
    {
        /// <summary>
        /// Validates the given value
        /// </summary>
        /// <param name="value">Value to validate</param>
        /// <returns>Validation result</returns>
        ValidationResult Validate(object value);
    }

    /// <summary>
    /// Result of a validation operation
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public ValidationSeverity Severity { get; set; }

        public static ValidationResult Success()
        {
            return new ValidationResult { IsValid = true };
        }

        public static ValidationResult Error(string message, ValidationSeverity severity = ValidationSeverity.Critical)
        {
            return new ValidationResult 
            { 
                IsValid = false, 
                ErrorMessage = message, 
                Severity = severity 
            };
        }
    }
}