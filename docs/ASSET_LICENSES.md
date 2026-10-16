# Procedência e licenças dos recursos incorporados

**Data:** 15/10/2026

O executável do Storiva incorpora apenas **dois** tipos de recurso gráfico: ícones Lucide e o ícone próprio da marca. Desde a Fase 3, ele também depende de duas bibliotecas de terceiros (seção 5). Nenhum ícone, bitmap, logotipo ou imagem de terceiros além desses foi incorporado.

## 1. Lucide

| Campo | Valor |
| --- | --- |
| Pacote | `lucide-static` **1.46.0** |
| Origem | Registro npm, `https://registry.npmjs.org/lucide-static/-/lucide-static-1.46.0.tgz` |
| Repositório | https://github.com/lucide-icons/lucide |
| Publicação | 14/09/2026, 09:38 UTC |
| Integridade (registro npm) | `sha512-kq1lP/78BxG28VG3Ut2FxMeQElereTrrRwTBeXH7+olxATGcDbsuKRB/v/VKLhPrEu8BvNi6Uh2GvbH3pQcYhg==` |
| Verificação | O sha512 do tarball baixado foi **igual** ao do registro |
| Licença | **ISC** (Lucide Icons and Contributors). Os ícones derivados do Feather também estão sob **MIT** (Cole Bemis) |
| Texto da licença | `src/Storiva.App/Assets/Icons/Lucide/LICENSE` (SHA-256 `b495047b…29c57`), copiado para a saída como `licenses/lucide-LICENSE.txt` |
| Aviso ao usuário | `THIRD_PARTY_NOTICES.md`, distribuído com o executável e exibido em Help › About / Third-party notices |
| Compatibilidade | ISC e MIT são permissivas e compatíveis com uso comercial e com WinForms/.NET 8. Exigem só manter o aviso de copyright, o que é feito |
| Uso | Os SVGs ficam embutidos como recurso (`Storiva.Icons.<nome>.svg`) e são renderizados pelo renderizador próprio (`SvgIconGeometry`). Não há biblioteca de terceiros envolvida |
| Modificações | Nenhuma nos arquivos. Na renderização, a cor do traço é definida pelo tema, e abaixo de 20 px a espessura é ajustada |

### Ícones incorporados (49)

Os ícones marcados com † aparecem na lista de derivados do Feather do arquivo LICENSE (MIT + ISC). Os hashes completos estão em `src/Storiva.App/Assets/Icons/Lucide/SHA256SUMS.txt`.

| Ícone | Uso no Storiva |
| --- | --- |
| arrow-left † | Voltar |
| arrow-right † | Avançar |
| arrow-up † | Subir um nível |
| check † | Apply changes (Permissions, Headers, Tags) |
| lock † | Make private (Permissions) |
| star | Favorito na barra de caminho |
| users | Make public (Permissions) |
| chevron-down † / chevron-right † | Expandir/recolher na árvore; separador do breadcrumb |
| circle-alert | Filtro "Failed" |
| circle-x | Cancelar tarefa |
| cloud-off | Indicador "Not connected"; conexão com erro |
| cloud | Indicador "Connected" na barra de status (Fase 3) |
| server | Cabeçalho do diálogo Edit Connection (Fase 3) |
| copy | Copiar localização |
| database † | Bucket |
| download † / upload † | Barra de ações: Download e Upload |
| external-link † | Barra de ações: Open |
| pencil | Editar a localização (campo de caminho) |
| plus † | Barra de buckets: New bucket / Add external bucket |
| trash-2 † | Barra de ações: Delete |
| file, file-text, file-image, file-archive, file-spreadsheet, file-code, file-video, file-music | Tipos de arquivo na lista |
| folder, folder-open, folder-plus | Pastas; nova pasta |
| house | Home (breadcrumb) |
| info † | Sobre |
| list | Filtro "All" |
| list-filter | Filtro da lista |
| loader † | Filtro "Queued"; nó carregando |
| log-out † | Sair |
| panel-left / panel-bottom | Mostrar/ocultar painéis |
| play | Filtro "Running"; iniciar tarefas |
| refresh-cw | Atualizar |
| rotate-ccw | Retry |
| search † | Pesquisa |
| settings | Options |
| square † | Filtro "Stopped"; parar tarefas |
| user-plus | Adicionar conta |
| x † | Limpar pesquisa |

## 2. Ícone da marca Storiva

| Campo | Valor |
| --- | --- |
| Arquivos | `src/Storiva.App/Assets/Brand/storiva.ico` (SHA-256 `e7caa8b4…48073`) e `storiva-256.png` (prévia) |
| Origem | **Criação original** deste projeto, gerada por `tools/generate-brand-icon.ps1` com GDI+ (formas geométricas: quadrado arredondado, três barras, um círculo) |
| Licença | Do projeto Storiva |
| Tamanhos | 16, 20, 24, 32, 40, 48, 64, 128 e 256 px (PNG dentro de ICO) |
| Relação com outras marcas | Nenhuma: desenho, cores e forma próprios |

## 3. Pacotes de teste (não distribuídos com o app)

| Pacote | Versão | Licença |
| --- | --- | --- |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 4.0.1 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT |

As versões começaram com as do template `xunit` do SDK 8.0.426 e foram atualizadas em 16/10/2026 para as últimas estáveis (xunit continua na série 2.x; o `xunit.runner.visualstudio` 4.x executa testes xUnit v2 e suporta .NET 8). Licenças conferidas no `.nuspec` de cada pacote (expressões `Apache-2.0` e `MIT`, sem mudança). Foram restauradas do nuget.org e não fazem parte do executável publicado. A atualização também eliminou as dependências transitivas vulneráveis `System.Net.Http` 4.3.0 e `System.Text.RegularExpressions` 4.3.0 (vindas de `NETStandard.Library` 1.6.1 pelo xunit 2.5.3).

## 4. Fonte

Segoe UI é uma fonte do sistema Windows. Ela não é distribuída com o Storiva.

## 5. Bibliotecas de terceiros (Fase 3, distribuídas com o app)

| Pacote | Versão | Licença | Autor | Procedência verificada |
| --- | --- | --- | --- | --- |
| AWSSDK.S3 | 4.0.104.4 (atualizado em 16/10/2026; antes 4.0.104) | Apache-2.0 | Amazon Web Services | nuget.org; pacote com assinatura de repositório do NuGet.org (`.signature.p7s`). Na versão 4.0.104, `dotnet nuget verify` confirmou a assinatura (válida até 18/05/2027); para 4.0.104.4 o comando não foi executado. Projeto https://github.com/aws/aws-sdk-net |
| AWSSDK.Core | 4.0.102.11 (dependência mínima do AWSSDK.S3 4.0.104.4; antes 4.0.102.8) | Apache-2.0 | Amazon Web Services | Idem |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | Microsoft | nuget.org; assinaturas de autor (Microsoft) e de repositório; série 8.0, compatível com .NET 8 (é a única versão 8.x; as séries 9.x e 10.x existem, mas o projeto fica na série do runtime) |

- Os textos das licenças acompanham o executável em `licenses\`:
  - `AWSSDK-Apache-2.0.txt`: texto oficial baixado de https://www.apache.org/licenses/LICENSE-2.0.txt, SHA-256 `cfc7749b96f63bd3…`;
  - `ProtectedData-MIT.txt`: `LICENSE.TXT` do próprio pacote, SHA-256 `d7a68596ab69b06f…`.
- Apache-2.0 e MIT são permissivas e compatíveis com uso comercial. Exigem manter os avisos, o que é feito em `THIRD_PARTY_NOTICES.md` e `licenses\`.
- Versões estáveis (sem sufixo de pré-lançamento), compatíveis com `net8.0`. Nenhuma outra dependência foi acrescentada. O `tests/Storiva.FakeS3` usa só a biblioteca padrão do .NET.
