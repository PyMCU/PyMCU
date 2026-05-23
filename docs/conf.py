import os
import sys

project = "PyMCU"
copyright = "2024, PyMCU Contributors"
author = "PyMCU Contributors"
release = "0.1"
version = "0.1"

extensions = [
    "myst_parser",
    "sphinx_copybutton",
    "sphinx_design",
    "sphinx.ext.intersphinx",
]

source_suffix = {
    ".rst": "restructuredtext",
    ".md": "markdown",
}

templates_path = ["_templates"]
exclude_patterns = ["_build", "Thumbs.db", ".DS_Store"]

# ---------------------------------------------------------------------------
# MyST extensions
# ---------------------------------------------------------------------------
myst_enable_extensions = [
    "colon_fence",
    "deflist",
    "tasklist",
    "attrs_inline",
]
myst_heading_anchors = 4

# ---------------------------------------------------------------------------
# HTML / PyData theme
# ---------------------------------------------------------------------------
html_theme = "pydata_sphinx_theme"
html_static_path = ["_static"]
html_css_files = ["css/custom.css"]
html_favicon = "_static/images/favicon.ico"
html_title = "PyMCU"

html_theme_options = {
    "logo": {
        "image_light": "_static/images/logo-light.svg",
        "image_dark": "_static/images/logo-dark.svg",
        "alt_text": "PyMCU",
    },
    "github_url": "https://github.com/pymcu/pymcu",
    "navbar_align": "left",
    "navbar_end": ["navbar-icon-links", "theme-switcher"],
    "secondary_sidebar_items": ["page-toc", "edit-this-page"],
    "show_prev_next": True,
    "navigation_with_keys": True,
    "footer_start": ["copyright"],
    "footer_end": ["sphinx-version"],
    "pygments_light_style": "friendly",
    "pygments_dark_style": "monokai",
    "header_links_before_dropdown": 6,
    "navigation_depth": 3,
    "show_nav_level": 1,
    "icon_links": [
        {
            "name": "GitHub",
            "url": "https://github.com/pymcu/pymcu",
            "icon": "fa-brands fa-github",
        },
        {
            "name": "PyPI",
            "url": "https://pypi.org/project/pymcu/",
            "icon": "fa-brands fa-python",
        },
    ],
}

html_sidebars = {
    "**": ["sidebar-nav-bs"],
}

# ---------------------------------------------------------------------------
# Intersphinx
# ---------------------------------------------------------------------------
intersphinx_mapping = {
    "python": ("https://docs.python.org/3", None),
}

# ---------------------------------------------------------------------------
# copybutton: strip prompt characters
# ---------------------------------------------------------------------------
copybutton_prompt_text = r">>> |\.\.\. |\$ "
copybutton_prompt_is_regexp = True
