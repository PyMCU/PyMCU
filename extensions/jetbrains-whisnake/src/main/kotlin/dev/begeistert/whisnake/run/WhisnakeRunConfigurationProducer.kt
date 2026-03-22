package dev.begeistert.whisnake.run

import com.intellij.execution.actions.ConfigurationContext
import com.intellij.execution.actions.LazyRunConfigurationProducer
import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.openapi.extensions.ExtensionPointName
import com.intellij.openapi.util.Ref
import com.intellij.psi.PsiElement
import dev.begeistert.whisnake.config.WhisnakeConfigReader

/**
 * Automatically produces a "Whisnake Build" run configuration when the user
 * right-clicks inside a pymcu project (pyproject.toml with [tool.whip] at root).
 */
@Suppress("UnstableApiUsage")
class WhisnakeRunConfigurationProducer : LazyRunConfigurationProducer<WhisnakeRunConfiguration>() {

    override fun getConfigurationFactory(): ConfigurationFactory {
        return WhisnakeRunConfigurationType().getFactory()
    }

    override fun setupConfigurationFromContext(
        configuration: WhisnakeRunConfiguration,
        context: ConfigurationContext,
        sourceElement: Ref<PsiElement>
    ): Boolean {
        val project = context.project
        // Only produce a config if this is a pymcu project
        WhisnakeConfigReader.findConfig(project) ?: return false
        configuration.name = "Whisnake Build"
        configuration.command = "build"
        return true
    }

    override fun isConfigurationFromContext(
        configuration: WhisnakeRunConfiguration,
        context: ConfigurationContext
    ): Boolean {
        if (configuration.command != "build") return false
        return WhisnakeConfigReader.findConfig(context.project) != null
    }
}
