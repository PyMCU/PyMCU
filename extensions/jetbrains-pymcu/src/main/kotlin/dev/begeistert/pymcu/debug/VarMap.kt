// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.debug

import com.intellij.openapi.diagnostic.Logger

data class VarScope(
    val function: String,
    val file: String,
    val startLine: Int,
    val vars: Map<String, String>,       // register vars: varName -> "R4"
    val varLines: Map<String, Int>,      // varName -> source line of first assignment
    val stackVars: Map<String, Int>,     // spilled vars: varName -> absolute SRAM address
    val stackVarLines: Map<String, Int>, // varName -> source line of first assignment (spilled)
    val params: Set<String>             // parameter variable names (never empty-guarded — empty = no params)
)

class VarMap(val scopes: List<VarScope>) {

    private val log = Logger.getInstance(VarMap::class.java)

    /** Returns the innermost scope that contains (file, line). */
    fun getScope(file: String, line: Int): VarScope? =
        scopes
            .filter { it.file == file && it.startLine <= line }
            .maxByOrNull { it.startLine }

    companion object {
        private val log = Logger.getInstance(VarMap::class.java)

        fun load(path: String): VarMap? = runCatching {
            val text   = java.io.File(path).readText()
            val scopes = parseVarMapJson(text)
            log.info("VarMap: loaded ${scopes.size} scopes from $path")
            VarMap(scopes)
        }.onFailure { log.warn("VarMap: failed to load $path: ${it.message}") }.getOrNull()

        // Splits the top-level JSON array into individual object strings by tracking brace depth.
        private fun splitTopLevelObjects(json: String): List<String> {
            val result = mutableListOf<String>()
            var depth  = 0
            var start  = -1
            for (i in json.indices) {
                when (json[i]) {
                    '{' -> { if (depth == 0) start = i; depth++ }
                    '}' -> {
                        depth--
                        if (depth == 0 && start >= 0) { result.add(json.substring(start, i + 1)); start = -1 }
                    }
                }
            }
            return result
        }

        // Parses: [{"Function":"...","File":"...","StartLine":N,"Vars":{...},"VarLines":{...},
        //           "StackVars":{...},"StackVarLines":{...},"Params":[...]},...]
        private fun parseVarMapJson(json: String): List<VarScope> {
            return splitTopLevelObjects(json).mapNotNull { obj ->
                val function  = SimpleJson.getString(obj, "Function")      ?: return@mapNotNull null
                val file      = SimpleJson.getString(obj, "File")          ?: return@mapNotNull null
                val startLine = SimpleJson.getInt(obj,    "StartLine")     ?: return@mapNotNull null
                val vars      = SimpleJson.getStringMap(obj, "Vars")       ?: emptyMap()
                val varLines  = SimpleJson.getIntMap(obj,   "VarLines")    ?: emptyMap()
                val stackVars = SimpleJson.getIntMap(obj,   "StackVars")   ?: emptyMap()
                val stackVarLines = SimpleJson.getIntMap(obj, "StackVarLines") ?: emptyMap()
                val params    = SimpleJson.getStringArray(obj, "Params").toSet()
                VarScope(function, file, startLine, vars, varLines, stackVars, stackVarLines, params)
            }
        }
    }
}
