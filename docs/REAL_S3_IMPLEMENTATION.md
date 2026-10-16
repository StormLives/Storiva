# Conexões reais (AWS S3, MinIO, endpoints S3 privados) e temas

**Data:** 15/10/2026 · **Versão:** 0.2.0 · Fase 3

Este documento registra a auditoria inicial, a arquitetura, as decisões e os riscos da Fase 3. Guias de uso:

- [CONNECTION_SETUP.md](CONNECTION_SETUP.md): conexões, região, path-style, teste de conexão e TLS.
- [CREDENTIAL_SECURITY.md](CREDENTIAL_SECURITY.md): armazenamento e limites da proteção.
- [THEMES.md](THEMES.md): Claro, Escuro e Sistema.
- [TESTING.md](TESTING.md): testes executados e como rodá-los.

## 1. Auditoria do projeto antes da Fase 3

| Área | Estado encontrado |
| --- | --- |
| Solução | `Storiva.Core` (net8.0), `Storiva.Infrastructure` (net8.0), `Storiva.App` (WinForms, net8.0-windows), `Storiva.Tests` (xUnit, 253 testes) |
| Dados | Só `DemoStorageCatalog` e `DemoObjectDetails`, em memória e síncronos (`IStorageCatalog`, `IObjectDetailsStore`) |
| Navegação | `BrowserNavigator` sobre `IStorageCatalog`; árvore com carregamento sob demanda; breadcrumb; voltar, avançar e subir |
| Menus | `CommandCatalog` + `CommandRules` + `ShortcutRouter`; 41 itens "não disponíveis" |
| Contas | `AccountRegistry`/`AccountValidator`: perfis só da sessão, **sem** credenciais, provedor real bloqueado |
| Tarefas | `TaskQueue`: simulação determinística por `Tick` |
| Abas | Oito abas ligadas ao `IObjectDetailsStore` demo (conteúdo fictício) |
| Tema | Paleta clara única (`StorivaPalette.Light`) capturada em campos `readonly` em várias classes |
| Persistência | Nenhuma (por decisão das fases 1 e 2) |
| Rede | Proibida; um teste de isolamento garantia a ausência de `System.Net*` |

**Real × simulado no início:** tudo era simulado ou local. Upload e download só criavam tarefas fictícias.

## 2. Arquitetura

```
Storiva.App (WinForms)
  MainForm (+ MainForm.Remote.cs, MainForm.Theme.cs)      ─ janela, comandos, abas
  Dialogs/ConnectionDialog, ConnectionsDialog, OptionsDialog
  Theme/ThemeService (IThemeService), ThemeApplier, NativeTheming, StorivaTabControl
        │ usa apenas contratos do Core
Storiva.Core (sem UI, sem rede)
  Connections/  ConnectionProfile, ConnectionRules, StorageCredentials, ISecretStore,
                IConnectionStore, ConnectionManager, ConnectionStatus, SecretRedactor
  Remote/       IObjectStorageClient (+ fábrica), StorageError(Kind), RemoteStorageCatalog,
                RemoteDetailsStore, RemoteOperations, LocalPathMapper
  Storage/      IStorageCatalog (+ SwitchableStorageCatalog)
  Tasks/        TaskQueue (só operações reais), OperationRunner
  Settings/     AppSettings, ThemePreference, ThemeResolver, ISettingsStore
        │ implementado por
Storiva.Infrastructure (net8.0-windows)
  S3/           S3ObjectStorageClient, S3ClientFactory, S3ErrorMapper   ← AWS SDK for .NET
  Persistence/  JsonSettingsStore, JsonConnectionStore, StorivaDataPaths
  Security/     DpapiSecretStore                                         ← ProtectedData (DPAPI)
```

### Como o modo real se encaixa sem reescrever a interface

1. A janela continua falando com um `IStorageCatalog` e um `IObjectDetailsStore`, agora somente leitura e *comutáveis* (`SwitchableStorageCatalog`, `SwitchableDetailsStore`): vazios (`EmptyStorageCatalog`, `EmptyDetailsStore`) sem conexão, ou os da conexão aberta.
2. Na conexão real, `RemoteStorageCatalog` é um **cache** do que já foi listado. Os membros síncronos só leem o cache; `LoadAsync` busca uma página de cada vez (no máximo 1000 itens, `ListObjectsV2` com delimitador `/`). Navegação, árvore, pesquisa, filtro e ordenação existentes funcionam sem mudança.
3. As escritas passam só por `IObjectStorageClient`, de forma assíncrona: cada comando chama o método `Remote*` correspondente em `MainForm.Remote.cs`. Os contratos de catálogo e detalhes não têm métodos de escrita.
4. As operações longas (upload, download, cópia, mover, excluir pasta, renomear, storage class) viram tarefas do `OperationRunner`: fila com limite de simultaneidade, cancelamento (`CancellationToken`), nova tentativa e progresso real. Esse progresso é limitado a cerca de 10 atualizações/s e é aplicado na thread da interface.
5. As abas inferiores pedem os dados sob demanda: só o aspecto da aba visível é carregado (ACL, HEAD, tags, versões ou os primeiros 4 KB), com um intervalo de 250 ms para não disparar uma requisição por linha ao navegar com as setas.

### `IObjectStorageClient`

`TestConnectionAsync`, `ListBucketsAsync`, `GetBucketRegionAsync`, `ListObjectsAsync` (página), `ListAllObjectsAsync` (todas as páginas), `CreateBucketAsync`, `DeleteBucketAsync`, `CreateFolderAsync`, `UploadObjectAsync`, `DownloadObjectAsync`, `DeleteObjectAsync`, `DeleteObjectsAsync`, `CopyObjectAsync`, `MoveObjectAsync` (cópia + exclusão, com resultado parcial), `GetObjectMetadataAsync`, `ReadObjectStartAsync`, `GetAclAsync`, `GetTagsAsync`/`SetTagsAsync`, `GetVersioningAsync`/`SetVersioningAsync`, `ListVersionsAsync`.

Todos os métodos são assíncronos, aceitam `CancellationToken` e, na falha, lançam `StorageOperationException` com um `StorageError` cuja mensagem não contém segredos.

## 3. Decisões

| # | Decisão | Motivo |
| --- | --- | --- |
| D-43 | AWS SDK for .NET **AWSSDK.S3 4.0.104** (+ AWSSDK.Core 4.0.102.8), Apache-2.0, pacotes com assinatura de repositório do NuGet.org | SDK oficial; assinatura SigV4, retries, multipart e TLS ficam com o SDK e o .NET |
| D-44 | **System.Security.Cryptography.ProtectedData 8.0.0** (MIT, Microsoft, assinatura de autor e de repositório) para DPAPI | Mecanismo do Windows para segredos por usuário; não há gerenciador de senhas próprio |
| D-45 | A Infrastructure passou a `net8.0-windows` | DPAPI só existe no Windows; o Storiva já era só para Windows |
| D-46 | ~~Modo demo preservado como fonte separada ("Offline demo" no menu Accounts)~~ — **revogada por D-69** | Requisito da época: dados fictícios separados das conexões reais |
| D-47 | O antigo `AccountRegistry`/`AddAccountDialog` (perfis sem credencial, só da sessão) foi **substituído** por `ConnectionManager` + `ConnectionDialog` + `ConnectionsDialog` | Dois modelos de conta confundiriam; os testes antigos foram trocados por testes de conexão |
| D-48 | Credenciais: chave de acesso (DPAPI), **perfil AWS nomeado** (SSO e assume-role; só o nome é salvo) ou **cadeia padrão do SDK** (somente AWS) | Prioriza credenciais temporárias e perfis, sem obrigar a guardar chaves permanentes |
| D-49 | A cadeia padrão é recusada para endpoints S3 compatíveis | Evita que credenciais AWS do ambiente sejam usadas com servidores de terceiros |
| D-50 | O endpoint exige esquema explícito; `http://` só com o consentimento "Allow unencrypted HTTP"; sem usuário, path ou query na URL | Requisito de TLS por padrão e de validação de URL |
| D-51 | **Nenhuma** personalização da validação de certificado (sem `ServerCertificateCustomValidationCallback`, `ServicePointManager` ou `DisableSSL`); um teste varre o código-fonte | Certificados inválidos nunca são aceitos em silêncio |
| D-52 | S3 compatível: path-style ligado por padrão; checksums flexíveis (CRC) **desligados** por padrão (`RequestChecksumCalculation/ResponseChecksumValidation = WHEN_REQUIRED`); ambos configuráveis por conexão | Compatibilidade com MinIO e outros servidores que rejeitam os novos cabeçalhos de checksum do SDK v4 |
| D-53 | AWS: um cliente por região de bucket. A região vem do `ListBuckets` (`BucketRegion`); diante de "região errada", o Storiva a descobre por `GetBucketLocation` e tenta **uma** vez | A primeira tentativa foi recusada, então nada foi alterado |
| D-54 | O teste de conexão é só leitura: lista a primeira página de buckets. `AccessDenied` nessa listagem = "Conectado (permissões limitadas)", porque uma chave ou assinatura inválida produz outros códigos | Requisito: distinguir credenciais inválidas de permissões limitadas sem operações destrutivas |
| D-55 | Exclusões **sempre** pedem confirmação (não há opção para desligar), nomeiam os itens e dizem se o versionamento torna a exclusão permanente | Requisito de confirmação de ações destrutivas |
| D-56 | Excluir bucket só funciona com o bucket vazio; o Storiva nunca esvazia um bucket | Requisito: nenhuma exclusão recursiva de bucket |
| D-57 | Mover = copiar tudo e só então excluir as origens. Falhas na cópia → nada é excluído. Falhas na exclusão → tarefa "Failed" informando que os objetos existem nos dois lugares | Requisito de falha parcial; nunca anunciar uma movimentação incompleta |
| D-58 | Downloads gravam em `nome.storiva-partial` e só renomeiam ao terminar; a chave é mapeada para um caminho seguro (`..`, caracteres inválidos e nomes reservados neutralizados; o caminho final é sempre verificado dentro da pasta escolhida) | Interrupções não deixam arquivos truncados; chaves maliciosas não escapam da pasta |
| D-59 | Progresso: porcentagem só quando o total é conhecido; caso contrário, barra listrada indeterminada e contagem | Requisito: nunca inventar uma porcentagem |
| D-60 | ACL e headers reais são **somente leitura**; tags reais são editáveis, com Apply + confirmação; Versions só lista versões (Restore/Delete desabilitados) | Editar ACL ou headers exige semântica que o Storiva ainda não implementa (cópia do objeto); ACL ≠ políticas |
| D-61 | Preview real: GET com `Range` dos primeiros 4 KB, mostrado como texto ou hex em `TextBox` (nunca renderizado nem executado); imagens, vídeo e áudio não são baixados para pré-visualização | Visualização segura |
| D-62 | Listagens são paginadas ("Load more"). Depois de uma escrita, a recarga relê as páginas que estavam abertas (no máximo 10 a mais) | Buckets grandes nunca são carregados inteiros; a visão do usuário não volta para a primeira página |
| D-63 | Persistência em `%LOCALAPPDATA%\Storiva` (não roaming): `settings.json` (tema), `connections.json` (sem segredos), `secrets\<id>.bin` (DPAPI) | Separação de configurações e segredos |
| D-64 | Arquivos JSON versionados e gravados de forma atômica; corrompidos → renomeados `.corrupt-<data>` e padrões usados; versão futura → lidos e **nunca sobrescritos**; entradas inválidas ou desconhecidas → ignoradas com aviso | Requisito de arquivos corrompidos e versões |
| D-65 | Importar ou carregar uma configuração nunca conecta. A sessão começa sempre desconectada (painel "Not connected"); só a ação do usuário (Connect, Test) abre uma conexão | Requisito: nenhuma conexão automática |
| D-66 | Tema: `IThemeService` + `StorivaPalette.Current` + `ThemeApplier` (mapeia cada cor atribuída pelo seu papel na paleta e restaura os padrões do WinForms ao voltar ao claro) | Mudança em tempo de execução sem espalhar verificações de tema pelos formulários |
| D-67 | Partes nativas: barra de título via `DWMWA_USE_IMMERSIVE_DARK_MODE` (documentado); barras de rolagem via `SetWindowTheme("DarkMode_Explorer")`; abas desenhadas pelo Storiva só no escuro (`StorivaTabControl`) | Sem transformar controles nativos em implementações frágeis; no claro, tudo continua nativo |
| D-68 | Testes do SDK real contra um **servidor S3 falso em 127.0.0.1** (`tests/Storiva.FakeS3`), inclusive TLS com certificado não confiável | Nenhuma chamada a endpoints externos nos testes automatizados |
| D-69 | **Modo demo removido** (15/10/2026, a pedido do usuário: "podemos tirar o modo demo, já terminamos"). Saíram `Infrastructure/Demo` (catálogo, detalhes e tarefas fictícias), os métodos de escrita de `IStorageCatalog`/`IObjectDetailsStore`, `HeaderRules`, as edições locais de ACL, a simulação da fila (`Tick`, Stop/Start, tarefas de exemplo), *Reset Demo Data*, *Pause Task Simulation* e as opções *Confirm before delete*, *Pause simulation* e *Verify simulated transfers*. Sem conexão, só Accounts, Options, Help e View funcionam; a lista mostra o painel "Not connected". Menu Accounts: as conexões salvas e "Disconnect from …"; Manage Accounts: botão *Disconnect* no lugar de *Use offline demo*. Os testes de navegação e listagem usam um catálogo em memória **só de teste** (`tests/Storiva.Tests/Fakes/InMemoryCatalog.cs`) | Pedido do usuário; elimina dados fictícios misturados a uma conexão real e o código duplicado de cada operação. Revoga D-46 |
| D-70 | Campos de texto: borda plana e fixa (cor `BorderStrong` da paleta) pintada pelo Storiva em `WM_NCPAINT` (`Theme/InputBorders.cs`), sem a borda de destaque do Windows 11 ao focar ou passar o mouse. Os campos usam `BorderStyle.Fixed3D`, porque o controle de edição desenha a borda `FixedSingle` dentro da área do texto | Pedido do usuário (16/10/2026): "os input ficaram com um active com borda, não gostei, retire isso" |
| D-71 | Preparação para o GitHub (15/10/2026): licença MIT, README em inglês e português com capturas de dados fictícios (`Storiva.exe --screenshot-tour`, `tools/capture-readme-screenshots.ps1`, servidor falso com `--sample`), CI no GitHub Actions e política de assinatura de código para a SignPath Foundation. Correções vistas nas capturas: o botão de overflow das barras (setinha quando os botões não cabem) era claro no tema escuro e agora segue a paleta; sem conexão, o foco do teclado caía no divisor e desenhava um pontilhado (agora vai para o painel de buckets, também ao desconectar); a aba Properties mostrava "(real data)", resto do modo demo, e agora mostra a linha "Connection"; os atalhos usam o foco lógico da janela, e não só o foco do Windows | Pedido do usuário: "vamos preparar o projeto para subirmos no github [...] pra gente poder pegar certificado [...] pra assinar o executável" |
| D-72-ux-1 | **Arrastar e soltar do Explorer** (`Main/MainForm.FileDrop.cs`): arquivos e pastas soltos na lista vão para o local atual; soltos num bucket (ou pasta) do painel esquerdo, para esse local, que é aberto e listado antes, para que a confirmação de substituição veja o que já existe. Usa o mesmo caminho de Files › Upload (`UploadLocalFiles`/`UploadLocalFolder`: mesmas tarefas, mesmas confirmações). Só com conexão e dentro de um bucket; fora disso o cursor é "proibido" e nada acontece. O Storiva só oferece *Copy* (o Explorer nunca apaga os originais), pergunta depois que o arrasto termina (`BeginInvoke`, o Explorer não fica preso no diálogo) e recusa unidades inteiras (`C:\`) e itens que não são arquivos locais | Pedido de melhoria de UX; um só caminho de upload evita duas implementações com regras diferentes |
| D-72-ux-2 | **Janela lembrada** em `settings.json` (`window`: posição e tamanho normais, maximizada, largura do painel de buckets e altura do painel inferior em pixels lógicos, aba inferior selecionada). A posição só é usada se a barra de título ainda estiver numa tela (`WindowLayoutRules`); senão a janela abre centralizada com o mesmo tamanho. Valores implausíveis são ignorados. Campos opcionais: o formato continua na versão 1 e arquivos antigos carregam sem aviso. Tema e janela compartilham um `SettingsSession`, para que um não sobrescreva o outro com uma cópia antiga. O smoke test usa a própria pasta de dados (apagada a cada execução), então o tamanho fixo inicial não muda | Pedido de melhoria de UX; nenhum segredo (só geometria e o id da conexão, que já está em connections.json) |
| D-72-ux-3 | **Última conexão usada** (`lastConnectionId`): só destacada no painel "Not connected" (primeira, em negrito, "— last used"). Continua valendo D-65: o Storiva sempre inicia desconectado e só conecta com um clique | Um clique a menos sem conexão automática |
| D-72-quality-1 | Dependências atualizadas (16/10/2026) para as últimas estáveis compatíveis com `net8.0`: AWSSDK.S3 4.0.104 → **4.0.104.4**, AWSSDK.Core 4.0.102.8 → **4.0.102.11** (Apache-2.0); testes: xunit 2.5.3 → **2.9.3** (série 2.x), xunit.runner.visualstudio 2.5.3 → **4.0.1** (Apache-2.0; executa testes v2), Microsoft.NET.Test.Sdk 17.8.0 → **18.11.0** (MIT). ProtectedData fica em **8.0.0** (única 8.x). Licenças conferidas nos `.nuspec`; `THIRD_PARTY_NOTICES.md` e `docs/ASSET_LICENSES.md` atualizados; textos de licença sem mudança | `dotnet list package --vulnerable --include-transitive` apontava `System.Net.Http` 4.3.0 e `System.Text.RegularExpressions` 4.3.0 (alta), transitivos do xunit 2.5.3 no projeto de testes; com xunit 2.9.3 não há mais pacote vulnerável. O app publicado não continha esses pacotes |
| D-72-quality-2 | Nomes locais de downloads (`LocalPathMapper.SafeName`): nomes de dispositivo com espaços antes da extensão (`CON .txt`), `COM0`/`LPT0`, `COM¹-³`/`LPT¹-³`, `CONIN$`/`CONOUT$` recebem `_` na frente; caracteres de controle bidirecional (U+202E etc.) viram `_`; o corte em 200 caracteres acontece antes de remover pontos e espaços finais e não parte pares substitutos | Em versões do Windows anteriores ao 11, `CON .txt` é tratado como o dispositivo CON (o download "termina" sem gravar nada); U+202E disfarça a extensão real (`fatura` + U+202E + `fdp.exe` aparece como `faturaexe.pdf`) |
| D-72-quality-3 | `LocalPathMapper.ToKey` aceita arquivos cujo nome começa com `..` (ex.: `..notes.txt`); só recusa `..` como segmento. `DownloadPrefixAsync` ignora (como falha) chaves que o servidor liste fora do prefixo pedido | Antes, um arquivo `..notes.txt` fazia o upload da pasta inteira falhar; um servidor S3 compatível malcomportado podia misturar outras chaves no download de uma pasta (sempre dentro da pasta escolhida) |
| D-72-quality-4 | Regiões vindas de respostas do servidor (`<Region>` de redirecionamentos, `BucketRegion` da listagem e do HEAD, `GetBucketLocation`) só são usadas quando passam em `ConnectionRules.ValidateRegion` | O nome da região vira parte do host do endpoint (`s3.<região>.amazonaws.com`); defesa em profundidade contra um valor malformado |
| D-72-quality-5 | `ConnectionProfile.SafeEndpointLabel` monta `esquema://host:porta` a partir de `Uri.Authority` | `GetLeftPart(UriPartial.Authority)` mantém `usuário:senha@`; perfis salvos nunca têm user info (validação), mas o rótulo agora cumpre o que a documentação promete |
| D-72-quality-6 | Analisadores .NET (`AnalysisLevel` `latest-recommended`) **não** foram ativados: a compilação de teste mostrou 255 avisos, 214 deles CA1707 (sublinhados nos nomes dos testes) e ~15 em `Main/` e `Dialogs/`, áreas editadas em paralelo por outros agentes. Corrigidos só os da área de qualidade: CA1847 (`S3ErrorMapper`) e CA1869 (`PreviewFormatter` reutiliza `JsonSerializerOptions`) | Ativar exigiria desligar CA1707/CA1711 nos testes e corrigir CA1822/CA1859/CA1305/CA1806/CA1826 no app, depois de integrar os outros ramos |

## 4. Sem conexão × conectado (estado final, após D-69)

O modo demo foi removido (D-69). Tudo o que o Storiva mostra vem do servidor da conexão aberta.

| Funcionalidade | Sem conexão | Conectado |
| --- | --- | --- |
| Lista e painel de buckets | Vazios; painel "Not connected" com links Connect / Add New Account.. / Manage Accounts.. | **Real**, paginada |
| Upload, download, criar pasta, excluir, copiar, mover, renomear | Desabilitados | **Real** (upload via TransferUtility, multipart acima de 16 MB; download em streaming para arquivo parcial) |
| Criar e excluir bucket; versionamento; Add External Bucket | Desabilitados | **Real** (excluir só se vazio; HEAD no bucket externo) |
| Change Storage Class | Desabilitado | **Real** (AWS: STANDARD, INTELLIGENT_TIERING, STANDARD_IA, ONEZONE_IA, GLACIER_IR; servidores compatíveis: sem escolha) |
| Permissions / Headers | Vazias | **Leitura** (ACL; HEAD + `x-amz-meta-*`) |
| Tags | Vazia | **Leitura e escrita** (confirmada) |
| Properties / Preview / Versions | Vazias | **Reais** (Preview: primeiros 4 KB; Versions: somente leitura) |
| Fila de tarefas | Vazia ao iniciar (sem tarefas de exemplo) | **Real** (estado vindo da operação; Cancel, Retry, Clear finished, Cancel All) |
| Accounts, Options, Help, temas | Funcionam | Funcionam |
| 41 itens "não disponíveis" (policy, CORS, lifecycle, IAM, URLs assinadas…) | Desabilitados | Aviso ("Storiva does not implement it yet.") |

## 5. Riscos e limitações conhecidos

| Risco | Situação |
| --- | --- |
| **Escritas não testadas contra AWS nem contra MinIO real** | Contra o MinIO do usuário rodou apenas o teste de conexão, somente leitura (15/10/2026: conectado em 1,7 s, path-style, us-east-1). Nenhuma operação de escrita foi executada num servidor real, e a AWS não foi testada. A interoperabilidade das demais operações foi testada com o SDK real contra o servidor S3 falso em loopback, que verifica o ID da chave, mas **não** valida assinaturas. Os testes de integração para MinIO/AWS existem e ficam ignorados até serem configurados (ver TESTING.md) |
| Multipart upload (> 16 MB) | Usa o TransferUtility do SDK. Não foi exercitado (o servidor falso não implementa multipart). Um upload multipart cancelado pode deixar partes pendentes no servidor; configure uma regra de ciclo de vida *AbortIncompleteMultipartUpload* |
| Cópias > 5 GB | `CopyObject` tem limite de 5 GB; o Storiva não divide cópias e informa o erro |
| DeleteObjects em servidores antigos | Alguns servidores exigem `Content-MD5`; o SDK v4 envia CRC32 nessa operação. Sintoma: erro "InvalidDigest"/"MissingContentMD5" |
| Redirecionamento de região na AWS | Implementado (uma nova tentativa), mas não testado contra a AWS |
| Pesquisa e filtro | Só sobre os itens já carregados (a barra de status avisa) |
| Pastas com nome vazio ("a//b") | São exibidas como "(blank name)"; as operações usam a chave exata |
| Detalhes após renomear | Relidos do servidor |
| DPAPI | Protege contra outro usuário ou outro computador; **não** protege contra código rodando como o mesmo usuário (ver CREDENTIAL_SECURITY.md) |
| Tema escuro | Algumas partes nativas continuam claras: caixas de mensagem, `TaskDialog`, seletores de arquivo, dicas de ferramenta, setas de combos. Ver THEMES.md |
| Proxy | Usa o proxy do sistema por meio do .NET; não há configuração própria |

## 6. Arquivos criados ou alterados na Fase 3

- **Core (novos):**
  - `Connections/ConnectionProfile.cs`, `Credentials.cs`, `ConnectionStatus.cs`, `ConnectionManager.cs`
  - `Remote/IObjectStorageClient.cs`, `StorageErrors.cs`, `RemoteStorageCatalog.cs`, `RemoteDetailsStore.cs`, `RemoteOperations.cs`, `LocalPathMapper.cs`
  - `Settings/AppSettings.cs`, `Tasks/OperationRunner.cs`
- **Core (alterados):** `Storage/IStorageCatalog.cs` (+ `SwitchableStorageCatalog`, `MayHaveChildContainers`), `StorageItem.cs` (`Key`, `ETag`), `Tasks/TaskQueue.cs`, `TransferTask.cs`, `Commands/SelectionContext.cs`, `CommandRules.cs`, `CommandCatalog.cs` (textos), `Details/PreviewFormatter.cs`.
- **Core (removido):** `Accounts/AccountProfile.cs` (D-47).
- **Infrastructure (novos):** `S3/S3ObjectStorageClient.cs`, `S3ClientFactory.cs`, `S3ErrorMapper.cs`, `Persistence/StorivaDataFiles.cs`, `JsonStores.cs`, `Security/DpapiSecretStore.cs`; o `.csproj` ganhou os pacotes e o alvo `net8.0-windows`.
- **App (novos):**
  - `Main/MainForm.Remote.cs`, `MainForm.Theme.cs`, `SmokeTest.Remote.cs`
  - `Dialogs/ConnectionDialog.cs`, `ConnectionsDialog.cs`
  - `Theme/ThemeService.cs`, `ThemeApplier.cs`, `StorivaTabControl.cs`
  - ícones `cloud.svg` e `server.svg`, `Assets/Licenses/`
- **App (alterados):**
  - `Program.cs`, `MainForm*.cs`
  - `Details/DetailsTabs.cs`
  - `Dialogs/OptionsDialog.cs`, `BucketDialogs.cs`, `StorivaDialog.cs`, `AboutDialog.cs`
  - `Commands/UserPrompts.cs`, `MenuBuilder.cs`
  - `Theme/StorivaTheme.cs`, `ControlStyles.cs`, `StorivaToolStripRenderer.cs`
  - `SmokeTest.cs`, `Icons/LucideIcons.cs`
- **App (removido):** `Dialogs/AddAccountDialog.cs` (D-47).
- **Testes:**
  - novo projeto `tests/Storiva.FakeS3`;
  - em `tests/Storiva.Tests`: `ConnectionTests.cs`, `PersistenceTests.cs`, `ThemeTests.cs`, `RemoteCatalogTests.cs`, `RemoteOperationTests.cs`, `S3ClientTests.cs`, `IntegrationTests.cs`, `Fakes/InMemoryObjectStorageClient.cs`, `AssemblyInfo.cs`;
  - `IconAndIsolationTests.cs` foi reescrito para a nova fronteira de rede;
  - `AccountAndActivityTests.cs` virou `ActivityTests.cs`.
- **Ferramentas:** `tools/validate.ps1` (servidor S3 falso para o smoke test e varredura de segredos), `Directory.Build.targets` (comentário).

## 7. Remoção do modo demo (D-69) — arquivos

- **Removidos:** `src/Storiva.Infrastructure/Demo/` (inteira).
- **Core:** `Storage/IStorageCatalog.cs` (só leitura + `EmptyStorageCatalog`), `Details/ObjectDetails.cs` (só leitura + `EmptyDetailsStore`), `Details/DetailRules.cs` (`AclRules` reduzida, `HeaderRules` removida), `Remote/RemoteStorageCatalog.cs`, `RemoteDetailsStore.cs`, `Commands/CommandId.cs`, `CommandCatalog.cs`, `CommandRules.cs`, `SelectionContext.cs`, `Settings/SessionOptions.cs`, `Tasks/TaskQueue.cs`, `TransferTask.cs`, `Details/PreviewFormatter.cs`.
- **App:** `Program.cs`, `Main/MainForm*.cs` (painel "Not connected", `Disconnect`), `Details/DetailsTabs.cs` (abas só com dados do servidor), `Dialogs/` (Bucket, NewFolder, KeyValue, Connections, Options, About), `SmokeTest*.cs`; novo `Theme/InputBorders.cs` (D-70).
- **Testes:** novo `Fakes/InMemoryCatalog.cs`; `TaskQueueTests.cs` reescrito; Details, Listing, Navigation, Storage, Command e RemoteOperation adaptados; os testes dos dados demo foram removidos.

## 8. Transferências: velocidade, tempo restante e novas tentativas (D-72-transfers)

| # | Decisão | Motivo |
| --- | --- | --- |
| D-72-transfers-1 | Uploads e downloads em andamento mostram na coluna Message a velocidade suavizada e o tempo restante ("4.2 MB/s · 12 s left"), calculados só do progresso real (`Core/Tasks/TransferRate.cs`): janelas de pelo menos 1 s e média móvel exponencial (peso 0,3), sem número antes da primeira janela completa. Exclusões e cópias (que contam objetos) não mostram velocidade. A ordem das colunas não mudou. Ao concluir, a mensagem ganha a duração e a média ("Saved to … (2 min 3 s, 10.1 MB/s on average)") quando levou pelo menos 1 s | Pedido de melhorias no motor de transferências; o usuário precisa saber se uma transferência grande está andando e quanto falta |
| D-72-transfers-2 | Nova tentativa automática por objeto (`Core/Remote/TransferRetry.cs`) só para falhas transitórias (`EndpointUnreachable`, `Throttled`, `ServerError`): 3 tentativas no total, esperas de 2 s e 8 s, canceláveis na hora; nunca para credenciais, permissão, validação, objeto inexistente, TLS ou erro de disco. O aviso aparece na mensagem da tarefa e no Event log ("Server error (HTTP 500): retrying 2/3 in 2 s…"); a falha final diz "(tried 3 times)". Um download refeito recomeça do zero no mesmo arquivo `.storiva-partial` (removido se falhar de vez). Numa pasta, depois que um objeto esgota as tentativas, os seguintes falham sem esperar até um sucesso (uma queda de rede não faz cada arquivo esperar 10 s) | O SDK já repete cada requisição HTTP (modo Standard, 3 tentativas, backoff curto), mas não cobre a queda da conexão no meio do corpo de um download nem quedas mais longas que o backoff dele. A camada da tarefa repete o objeto inteiro, com esperas maiores, sem duplicar o que o SDK faz |
| D-72-transfers-3 | `S3ClientFactory` fixa `RetryMode = Standard` (já era o padrão do SDK v4) junto com `MaxErrorRetry = 2`, para que `AWS_RETRY_MODE`/`AWS_MAX_ATTEMPTS` ou o arquivo de configuração da AWS não mudem o comportamento. `S3ErrorMapper`: um timeout HTTP (`TaskCanceledException` com `TimeoutException` interna) agora é "Endpoint not reachable" e não "cancelado pelo usuário"; uma conexão encerrada no meio do corpo (`HttpIOException`) é erro de rede, e não "Local file error" | Sem isso, um timeout aparecia como cancelamento e a queda no meio do download como erro de disco, e nenhum dos dois seria repetido |

Testes: `TransferTests.cs` (velocidade, formatação, mensagens, regras de nova tentativa, cancelamento durante a espera, arquivo parcial) e, com o SDK real contra o `FakeS3Server`, conexão cortada no meio do corpo (`TruncatedKeys`) e erros 500 além dos retries do SDK (`FailingKeys`). Retomar um download a partir do byte já recebido (GET com Range + If-Match) fica para depois: exigiria mudar `IObjectStorageClient`.
