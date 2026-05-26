// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.debug

import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.xdebugger.XDebuggerManager

/**
 * "Step Instruction" action — advances the AVR emulator by exactly one machine
 * instruction regardless of whether it maps to a Python source line.
 *
 * Shows in the XDebugger top toolbar while a PyMCU debug session is active.
 * Use this to walk through inline-expanded functions, @inline HAL methods, or
 * any asm() block one AVR opcode at a time.
 */
class PyMcuStepInstructionAction : AnAction() {

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val process = XDebuggerManager.getInstance(project)
            .currentSession?.debugProcess as? PyMcuDebugProcess ?: return
        process.stepInstruction()
    }

    override fun update(e: AnActionEvent) {
        val project = e.project
        val process = if (project != null)
            XDebuggerManager.getInstance(project).currentSession?.debugProcess as? PyMcuDebugProcess
        else null
        e.presentation.isEnabledAndVisible = process != null
    }
}
