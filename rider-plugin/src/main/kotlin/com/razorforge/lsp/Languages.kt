package com.razorforge.lsp

import com.intellij.lang.Language
import com.intellij.openapi.fileTypes.LanguageFileType
import javax.swing.Icon

/**
 * The two file languages this plugin contributes. They carry no PSI/parser of their own — the
 * IntelliJ LSP client handles semantics — but a registered [Language] + [LanguageFileType] is
 * what makes Rider open `.rf`/`.sf` as editable text and gives the LSP server support provider a
 * concrete file type to attach to.
 */
object RazorForgeLanguage : Language("RazorForge")

object SuflaeLanguage : Language("Suflae")

object RazorForgeFileType : LanguageFileType(RazorForgeLanguage) {
    override fun getName(): String = "RazorForge"
    override fun getDescription(): String = "RazorForge source file"
    override fun getDefaultExtension(): String = "rf"
    override fun getIcon(): Icon? = null
}

object SuflaeFileType : LanguageFileType(SuflaeLanguage) {
    override fun getName(): String = "Suflae"
    override fun getDescription(): String = "Suflae source file"
    override fun getDefaultExtension(): String = "sf"
    override fun getIcon(): Icon? = null
}
