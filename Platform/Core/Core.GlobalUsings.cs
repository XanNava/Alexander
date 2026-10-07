// The command contracts (ParsedCommand, CommandDefinition,
// CommandValidationException, IServiceCommandLayer, ...) live in
// Alexander.Service so service layers can implement them without
// referencing Core. Imported once here instead of in every handler.
global using Alexander.Services.Contracts;
