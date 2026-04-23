package dev.begeistert.pymcu.run

import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.execution.configurations.ConfigurationType
import com.intellij.execution.configurations.RunConfiguration
import com.intellij.icons.AllIcons
import com.intellij.openapi.project.Project
import javax.swing.Icon

/**
 * Registers the "PyMCU" run configuration type in the IDE's run/debug framework.
 */
class PyMCURunConfigurationType : ConfigurationType {

    private val factory = object : ConfigurationFactory(this) {
        override fun getId(): String = "PyMCUConfigurationFactory"

        override fun createTemplateConfiguration(project: Project): RunConfiguration =
            PyMCURunConfiguration(project, this, "PyMCU")

        override fun getName(): String = "PyMCU"
    }

    override fun getDisplayName(): String = "PyMCU"

    override fun getConfigurationTypeDescription(): String =
        "Run pymcu build, flash, or clean commands"

    override fun getIcon(): Icon = AllIcons.RunConfigurations.Application

    override fun getId(): String = "PyMCURunConfiguration"

    override fun getConfigurationFactories(): Array<ConfigurationFactory> = arrayOf(factory)

    fun getFactory(): ConfigurationFactory = factory
}
