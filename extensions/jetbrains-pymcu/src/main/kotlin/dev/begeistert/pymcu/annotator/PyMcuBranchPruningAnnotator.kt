// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.annotator

import com.intellij.lang.annotation.AnnotationHolder
import com.intellij.lang.annotation.Annotator
import com.intellij.lang.annotation.HighlightSeverity
import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.EditorColorsManager
import com.intellij.openapi.editor.markup.TextAttributes
import com.intellij.psi.PsiElement
import com.jetbrains.python.psi.*
import dev.begeistert.pymcu.config.PyMcuConfigReader

/**
 * Greys out match/case branches that the PyMCU compiler will discard via
 * compile-time branch pruning (i.e. `match __CHIP__.arch:` / `match __CHIP__.name:`
 * blocks where the case value doesn't match the project's target chip).
 *
 * Uses enforcedTextAttributes so the grey overrides individual token syntax
 * colours (keywords, strings, operators) that would otherwise stay coloured.
 */
class PyMcuBranchPruningAnnotator : Annotator {

    override fun annotate(element: PsiElement, holder: AnnotationHolder) {
        if (element !is PyCaseClause) return

        val matchStmt   = element.parent as? PyMatchStatement ?: return
        val subject     = matchStmt.subject ?: return
        val subjectText = subject.text.trim()

        val attr = when (subjectText) {
            "__CHIP__.arch"              -> "arch"
            "__CHIP__.name", "__CHIP__.chip" -> "name"
            "__CHIP__"                   -> "chip"
            else                         -> return
        }

        val config = PyMcuConfigReader.findConfig(element.project) ?: return
        val chip   = config.chip ?: return

        val targetValue = when (attr) {
            "arch" -> chipToArch(chip) ?: return
            else   -> chip
        }

        val pattern = element.pattern ?: return

        if (patternCouldMatch(pattern, targetValue)) return

        // enforcedTextAttributes overrides individual token syntax colours so the
        // entire dead branch — including keywords, strings and operators — appears grey.
        holder.newSilentAnnotation(HighlightSeverity.INFORMATION)
            .range(element.textRange)
            .enforcedTextAttributes(prunedAttrs())
            .create()
    }

    private fun patternCouldMatch(pattern: PyPattern, value: String): Boolean = when (pattern) {
        is PyWildcardPattern -> true
        is PyCapturePattern  -> true
        is PyLiteralPattern  -> {
            val expr = pattern.expression
            if (expr is PyStringLiteralExpression) expr.stringValue == value
            else expr.text.trim('"', '\'') == value
        }
        is PyOrPattern -> pattern.alternatives.any { patternCouldMatch(it, value) }
        else           -> false
    }

    private fun chipToArch(chip: String): String? = when {
        chip.startsWith("atmega") || chip.startsWith("attiny") -> "avr"
        chip.startsWith("pic")  -> "pic"
        chip.startsWith("rv")   -> "riscv"
        chip.startsWith("pio")  -> "pio"
        else                    -> null
    }

    companion object {
        // Derive the foreground colour from the active scheme's LINE_COMMENT so it
        // respects dark/light themes without requiring a registered colour setting.
        private fun prunedAttrs(): TextAttributes {
            val scheme = EditorColorsManager.getInstance().globalScheme
            val fg = scheme.getAttributes(DefaultLanguageHighlighterColors.LINE_COMMENT)
                         ?.foregroundColor
            return TextAttributes(fg, null, null, null, 0)
        }
    }
}
