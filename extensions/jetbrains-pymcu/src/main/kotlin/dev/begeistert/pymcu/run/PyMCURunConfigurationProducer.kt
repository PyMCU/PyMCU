package dev.begeistert.pymcu.run

import com.intellij.execution.actions.ConfigurationContext
import com.intellij.execution.actions.LazyRunConfigurationProducer
import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.openapi.extensions.ExtensionPointName
import com.intellij.openapi.util.Ref
import com.intellij.psi.PsiElement
import dev.begeistert.pymcu.config.PyMCUConfigReader

/**
 * Automatically produces a "PyMCU Build" run configuration when the user
 * right-clicks inside a pymcu project (pyproject.toml with [tool.pymcu] at root).
 */
@Suppress("UnstableApiUsage")
class PyMCURunConfigurationProducer : LazyRunConfigurationProducer<PyMCURunConfiguration>() {

    override fun getConfigurationFactory(): ConfigurationFactory {
        return PyMCURunConfigurationType().getFactory()
    }

    override fun setupConfigurationFromContext(
        configuration: PyMCURunConfiguration,
        context: ConfigurationContext,
        sourceElement: Ref<PsiElement>
    ): Boolean {
        val project = context.project
        // Only produce a config if this is a pymcu project
        PyMCUConfigReader.findConfig(project) ?: return false
        configuration.name = "PyMCU Build"
        configuration.command = "build"
        return true
    }

    override fun isConfigurationFromContext(
        configuration: PyMCURunConfiguration,
        context: ConfigurationContext
    ): Boolean {
        if (configuration.command != "build") return false
        return PyMCUConfigReader.findConfig(context.project) != null
    }
}
