# -----------------------------------------------------------------------------
# PyMCU Toolchain SDK
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: AGPL-3.0-or-later
# -----------------------------------------------------------------------------

"""
pymcu-toolchain-sdk — public API surface for PyMCU toolchain plugin packages.

Import from this package to access the stable base classes and plugin protocol::

    from pymcu_toolchain_sdk import (
        CacheableTool,
        ExternalToolchain,
        HardwareProgrammer,
        ToolchainPlugin,
        _default_platform_key,
        _is_non_interactive,
        _tool_lock,
    )
"""

from .base_tool import (
    CacheableTool,
    _default_platform_key,
    _is_non_interactive,
    _tool_lock,
)
from .toolchain import ExternalToolchain
from .programmer import HardwareProgrammer
from .plugin import ToolchainPlugin

__all__ = [
    "CacheableTool",
    "ExternalToolchain",
    "HardwareProgrammer",
    "ToolchainPlugin",
    "_default_platform_key",
    "_is_non_interactive",
    "_tool_lock",
]
