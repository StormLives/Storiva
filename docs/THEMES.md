# Temas: Claro, Escuro e Sistema

## Como mudar

**Tools › Options.. (Alt+O) › Interface › Appearance**:

- **System**: acompanha a configuração do Windows (*Configurações › Personalização › Cores › Modo de aplicativo*). Quando o Windows troca entre claro e escuro, o Storiva troca junto, **sem reiniciar**. É o padrão.
- **Light**: a paleta original do Storiva.
- **Dark**: a paleta escura.

A escolha é salva em `%LOCALAPPDATA%\Storiva\settings.json`. Se o arquivo não puder ser gravado, o tema vale para a sessão e o Options mostra o motivo.

## Arquitetura

| Peça | Papel |
| --- | --- |
| `ThemePreference` / `ThemeResolver` (Core) | Preferência e regra pura: System + Windows escuro → Dark; configuração do Windows ilegível → Light |
| `IThemeService` / `ThemeService` (App) | Preferência, tema efetivo, detecção do Windows (`HKCU\…\Themes\Personalize\AppsUseLightTheme`), notificação (`Changed`) e persistência. Ouve `SystemEvents.UserPreferenceChanged` e repassa para a thread da interface |
| `StorivaPalette.Light` / `.Dark` / `.Current` | Única fonte de cores. Renderizador de menus e barras, grades, árvore, abas, diálogos e ícones leem `Current` |
| `ThemeApplier` | Recolore as janelas abertas. Cada cor atribuída é mapeada pelo seu **papel** na paleta (fundo, texto, borda…). As cores-padrão do WinForms (SystemColors) trocadas no escuro são guardadas e **restauradas exatamente** ao voltar ao claro |
| `NativeTheming` | Barra de título escura (`DWMWA_USE_IMMERSIVE_DARK_MODE`, documentado) e barras de rolagem escuras (`SetWindowTheme("DarkMode_Explorer")`) |
| `StorivaTabControl` | No escuro, desenha a faixa de abas pela paleta (o TabControl nativo ignora `BackColor`); no claro é o controle nativo de antes |
| `InputBorders` | Campos de texto de uma linha: o Storiva pinta a moldura (`WM_NCPAINT`) com uma linha `BorderStrong` fixa, nos dois temas, sem a borda de destaque do Windows 11 no foco ou no hover (D-70). Por isso os campos usam `BorderStyle.Fixed3D` |

Os formulários não verificam o tema nos seus eventos. A janela principal reage a `Changed` em um único lugar (`MainForm.Theme.cs`). Ali ela recolore, renderiza os ícones de novo (o mesmo caminho de uma mudança de DPI) e redesenha.

## Paleta escura

Superfícies neutras e nunca preto absoluto; a identidade *teal* foi mantida, com tons mais claros para manter o contraste:

| Papel | Claro | Escuro |
| --- | --- | --- |
| Chrome (janela, menus, status) | `#F3F5F6` | `#181D20` |
| Surface (listas, árvore, diálogos) | `#FFFFFF` | `#20262A` |
| Toolbar | `#F8FAFA` | `#1C2225` |
| Border / BorderStrong | `#D6DDE0` / `#A7B3B8` | `#343D42` / `#4F5B61` |
| TextPrimary / TextSecondary | `#1C2428` / `#55626A` | `#E4EAEC` / `#A3B1B7` |
| Accent / Link | `#0E7C78` / `#0A6966` | `#3DB5AC` / `#5FCEC5` |
| Seleção com foco | `#C6E7E3` | `#1E4F4B` |
| Danger / Warning / Success | `#B42318` / `#A15C07` / `#16794A` | `#F2766B` / `#E8A94F` / `#4CC38A` |

Os testes (`PaletteTests`) verificam, nos dois temas:

- texto principal ≥ 7:1;
- texto secundário, links, erros e texto selecionado ≥ 4,5:1;
- avisos e ícones ≥ 3:1;
- no escuro, superfícies distintas entre si e nenhuma em preto puro.

Os ícones Lucide são renderizados de novo com as cores do tema.

## Limitações

Partes desenhadas pelo Windows que não seguem a paleta:

- `MessageBox`, `TaskDialog` (conflito de download), `OpenFileDialog`/`SaveFileDialog`/`FolderBrowserDialog`: seguem o Windows, não o Storiva;
- dicas de ferramenta (*tooltips*) dos menus e barras;
- seta e lista suspensa das caixas de combinação (o campo fica escuro com `FlatStyle.Flat`);
- marcadores de `CheckBox`/`RadioButton` (o texto acompanha o tema);
- as barras de rolagem escuras usam o nome de tema do Explorer; em Windows antigos continuam claras;
- a barra de título escura exige Windows 10 1809 ou superior;
- temas de **alto contraste** do Windows não são respeitados pela paleta própria.

Um diálogo modal aberto no momento em que o Windows troca de modo é recolorido. Diálogos criados depois já nascem no tema novo.
