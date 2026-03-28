# Whipsnake Board Definitions Package
# Board files map logical board pin names to the underlying chip register strings.
# The target chip is determined by [tool.pymcu] chip = "..." in pyproject.toml.
# Each board file validates the target chip via match __CHIP__.arch / __CHIP__.name.
