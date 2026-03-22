package dev.begeistert.whipsnake.run

import com.intellij.execution.actions.ConfigurationContext
import com.intellij.execution.actions.LazyRunConfigurationProducer
import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.openapi.extensions.ExtensionPointName
import com.intellij.openapi.util.Ref
import com.intellij.psi.PsiElement
import dev.begeistert.whipsnake.config.WhipsnakeConfigReader

/**
 * Automatically produces a "Whipsnake Build" run configuration when the user
 * right-clicks inside a pymcu project (pyproject.toml with [tool.whip] at root).
 */
@Suppress("UnstableApiUsage")
class WhipsnakeRunConfigurationProducer : LazyRunConfigurationProducer<WhipsnakeRunConfiguration>() {

    override fun getConfigurationFactory(): ConfigurationFactory {
        return WhipsnakeRunConfigurationType().getFactory()
    }

    override fun setupConfigurationFromContext(
        configuration: WhipsnakeRunConfiguration,
        context: ConfigurationContext,
        sourceElement: Ref<PsiElement>
    ): Boolean {
        val project = context.project
        // Only produce a config if this is a pymcu project
        WhipsnakeConfigReader.findConfig(project) ?: return false
        configuration.name = "Whipsnake Build"
        configuration.command = "build"
        return true
    }

    override fun isConfigurationFromContext(
        configuration: WhipsnakeRunConfiguration,
        context: ConfigurationContext
    ): Boolean {
        if (configuration.command != "build") return false
        return WhipsnakeConfigReader.findConfig(context.project) != null
    }
}
