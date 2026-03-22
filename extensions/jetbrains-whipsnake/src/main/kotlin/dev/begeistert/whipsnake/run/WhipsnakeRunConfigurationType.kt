package dev.begeistert.whipsnake.run

import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.execution.configurations.ConfigurationType
import com.intellij.execution.configurations.RunConfiguration
import com.intellij.icons.AllIcons
import com.intellij.openapi.project.Project
import javax.swing.Icon

/**
 * Registers the "Whipsnake" run configuration type in the IDE's run/debug framework.
 */
class WhipsnakeRunConfigurationType : ConfigurationType {

    private val factory = object : ConfigurationFactory(this) {
        override fun getId(): String = "WhipsnakeConfigurationFactory"

        override fun createTemplateConfiguration(project: Project): RunConfiguration =
            WhipsnakeRunConfiguration(project, this, "Whipsnake")

        override fun getName(): String = "Whipsnake"
    }

    override fun getDisplayName(): String = "Whipsnake"

    override fun getConfigurationTypeDescription(): String =
        "Run whip build, flash, or clean commands"

    override fun getIcon(): Icon = AllIcons.RunConfigurations.Application

    override fun getId(): String = "WhipsnakeRunConfiguration"

    override fun getConfigurationFactories(): Array<ConfigurationFactory> = arrayOf(factory)

    fun getFactory(): ConfigurationFactory = factory
}
