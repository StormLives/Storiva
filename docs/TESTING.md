# Testes

## Visão geral

| Camada | Onde | Rede | Quando roda |
| --- | --- | --- | --- |
| Testes unitários | `tests/Storiva.Tests` | Nenhuma | Sempre (`dotnet test`) |
| Interoperabilidade com o AWS SDK | `tests/Storiva.Tests/S3ClientTests.cs` + `tests/Storiva.FakeS3` | **Somente 127.0.0.1** | Sempre |
| Smoke test da janela real | `Storiva.exe --smoke-test <pasta>` | 127.0.0.1, quando o servidor falso está ativo | `tools\validate.ps1` |
| Capturas do README | `Storiva.exe --screenshot-tour <pasta>` | 127.0.0.1 (servidor falso com `--sample`: buckets `example-*` fictícios) | `tools\capture-readme-screenshots.ps1` (copia para `docs/images`) |
| CI | `.github/workflows/build.yml` | Nenhuma além do GitHub | Build Release, testes e publish a cada push e pull request |
| Integração com MinIO/AWS de teste | `tests/Storiva.Tests/IntegrationTests.cs` | O endpoint que **você** configurar | Só com as variáveis `STORIVA_IT_*` |

Nenhum teste automatizado usa credenciais reais nem endpoints externos. As credenciais de teste são valores fixos e fictícios (`STORIVATESTKEY` / `storiva-fake-secret-not-a-real-credential`).

## Executar

```powershell
# tudo: clean, build Debug/Release, testes Debug/Release, publish, smoke test do exe publicado com o servidor S3 falso,
# varredura de segredos na pasta de dados do smoke test
powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate.ps1

# só os testes
dotnet test Storiva.sln
```

## O que é testado

| Arquivo | Assunto |
| --- | --- |
| `ConnectionTests.cs` | Validação de endpoint (esquema obrigatório; http só com consentimento; sem usuário, path ou query), região, perfil, chaves; `ToString` redigido; `SecretRedactor`; `ConnectionManager` (segredo separado, manter chave ao editar, remover apaga o segredo, teste registrado e invalidado ao mudar a configuração, falhas → estados, rascunho inválido não gera requisição, segredo ilegível → erro seguro, arquivo somente leitura) |
| `PersistenceTests.cs` | DPAPI: ida e volta, arquivo sem texto puro (UTF-8/UTF-16), blob preso ao id, arquivo danificado. JSON: tema, arquivo corrompido renomeado, versão futura somente leitura, entradas inválidas ou desconhecidas ignoradas, `connections.json` sem campos secretos, pasta padrão `%LOCALAPPDATA%\Storiva` |
| `ThemeTests.cs` | `ThemeResolver`; `ThemeService` (preferência salva, System segue o Windows, Light/Dark ignoram o Windows, falha ao salvar → aviso); contraste WCAG das paletas; `ThemeApplier` claro → escuro → claro restaura as cores exatas |
| `RemoteCatalogTests.cs` | Listagem por prefixo, chaves exatas e nomes incomuns (`a//b`, `a b+c%d`), marcador de pasta ignorado, paginação, recarga das páginas abertas, `Exists`/`Find`, invalidação, escrita síncrona proibida; `RemoteDetailsStore` (carga sob demanda, cache, ACL → linhas, falhas como mensagens, versões só com versionamento, tags salvas explicitamente); `LocalPathMapper` (nomes reservados, `..`, caminho nunca sai da pasta) |
| `RemoteOperationTests.cs` | Excluir prefixo (só a pasta; falhas reportadas); mover (nada é excluído se uma cópia falhar; exclusão falha → "parcial"); copiar ou mover para dentro de si mesmo é recusado; download via arquivo parcial; cancelamento não deixa arquivo; conflitos (manter os dois, sobrescrever, pular); download de prefixo com chave maliciosa; upload de pasta; regras de comando remotas; `OperationRunner` (progresso real, indeterminado, cancelar em execução e na fila, falha + nova tentativa, limite de simultaneidade, simulação não toca tarefas reais) |
| `S3ClientTests.cs` | **SDK real ↔ servidor falso**: teste de conexão só com GET; listagem negada → permissões limitadas; chave desconhecida e assinatura rejeitada → credenciais inválidas; porta fechada → inacessível; **certificado TLS não confiável → erro TLS, nenhuma requisição aceita**; delimitador e paginação; chaves Unicode; upload/download de 300 KB com progresso; pasta, DeleteObjects parcial, bucket não vazio, criar e excluir bucket, 404; ACL, tags, versionamento; storage class; AccessDenied em escrita. Configuração do SDK (ServiceURL, path-style, região de assinatura, checksums). `S3ErrorMapper` (códigos → tipos; transporte; mensagens sem segredos) |
| `IconAndIsolationTests.cs` | 49 ícones; Core e App sem bibliotecas de rede nem do AWS SDK; a rede só passa pelo SDK, na Infrastructure; varredura do código-fonte: **nenhum** desvio de validação TLS e nenhuma interpolação de segredos |
| `TaskQueueTests.cs` | Fila só com operações reais: começa vazia, ids e tamanho, Cancel pede a parada uma vez, Retry só em falha/cancelamento, eventos, limite de simultaneidade, filtros com estados vindos do `OperationRunner` |
| `StorageTests.cs`, `DetailsTests.cs` | `StoragePath`, nomes de pasta e bucket, tags, `EditSession`, `PreviewFormatter`, favoritos, opções, Event log; catálogo e detalhes **vazios** sem conexão; catálogo comutável |
| `NavigationTests.cs`, `ListingTests.cs` | Navegador (histórico, subir, breadcrumb, entradas apagadas) e pesquisa/filtro/ordenação sobre `Fakes/InMemoryCatalog.cs`, um catálogo em memória **só de teste** (o app não tem dados de exemplo) |
| `CommandTests.cs` | Regras dos menus [F E4–E6] por seleção e foco; sem conexão, só Accounts/Options/Help/View |

## Smoke test da janela

`tools\validate.ps1` inicia `tests\Storiva.FakeS3` (`--seed`: buckets `storiva-smoke`, `storiva-empty` e o oculto `storiva-hidden`; um objeto lento para o teste de cancelamento). Ele passa o endpoint ao smoke test por `STORIVA_SMOKE_S3_*` e encerra o servidor no final. O smoke test usa uma pasta de dados própria (`<saída>\data`) e um tema do Windows fixo; os dados e o tema do usuário não são tocados.

A seção `[remote]` passa pelas mesmas entradas que um usuário:

- salvar uma conexão, sem segredo no JSON e com DPAPI;
- testar: conectado, chave desconhecida e porta fechada;
- conectar e listar buckets;
- paginar com "Load more" (página de 2);
- pesquisa local e breadcrumb;
- Preview, Properties, Permissions e Versions com dados do servidor;
- upload com progresso real e download byte a byte idêntico, sem arquivo parcial;
- cancelar um download em andamento;
- tags confirmadas;
- criar pasta, copiar e colar, excluir com confirmação;
- renomear arquivo e pasta, recortar e colar;
- enviar e baixar pasta;
- criar bucket com versionamento, suspender o versionamento, excluir bucket vazio;
- Add External Bucket, conhecido e desconhecido;
- excluir bucket não vazio: a recusa é mostrada;
- Event log com erros e sem segredos;
- Manage Connections;
- menus por contexto e atalhos, aviso "não disponível", ordenação, filtros, favoritos e formato do Event log;
- desconectar: volta ao painel "Not connected", com o link para a conexão salva.

As seções `[layout]` e `[menus]` rodam **sem conexão**: janela vazia, painel "Not connected", nenhuma tarefa, comandos de dados desabilitados. Não há dados de exemplo.

As seções `[theme]` e `[dialogs]` cobrem:

- Claro → Escuro (cores aplicadas, contraste, barra de título lida do DWM, preferência salva);
- System seguindo o Windows nos dois sentidos;
- volta às cores claras exatas;
- escolha do tema no Options;
- validações do diálogo de conexão;
- borda dos campos de texto focados (claro e escuro): cor real lida de uma captura só da janela.

As capturas usam `DrawToBitmap` ou `PrintWindow`, que renderizam **só a janela do Storiva**. Não há captura da tela, que poderia incluir outras janelas.

## Testes de integração com MinIO ou AWS de teste

Ficam **ignorados** até você configurar um ambiente isolado:

```powershell
$env:STORIVA_IT_PROVIDER   = 'compatible'                          # ou 'aws'
$env:STORIVA_IT_ENDPOINT   = 'https://minio.test.internal:9000'     # só para 'compatible'
$env:STORIVA_IT_REGION     = 'us-east-1'
$env:STORIVA_IT_ACCESS_KEY = '<chave de um usuário de teste>'
$env:STORIVA_IT_SECRET_KEY = '<segredo desse usuário>'
$env:STORIVA_IT_BUCKET     = 'storiva-integration'                  # bucket dedicado a testes
$env:STORIVA_IT_ALLOW_WRITES = '1'                                  # opcional
dotnet test Storiva.sln --filter FullyQualifiedName~IntegrationTests
```

- **Sem** `STORIVA_IT_ALLOW_WRITES`: só conecta e lista.
- **Com** ele: cria objetos apenas sob `storiva-it/<guid>/` e, no final, exclui **exatamente** os objetos que criou. Nunca cria ou exclui buckets.

Para um MinIO local descartável (exemplo; credenciais de teste geradas por você, nunca reutilize uma senha real):

```powershell
docker run --rm -p 9000:9000 -e MINIO_ROOT_USER=storiva-test -e MINIO_ROOT_PASSWORD=<senha-de-teste-longa> minio/minio server /data
```

Esse servidor usa `http://`; marque *Allow unencrypted HTTP* e *path-style* na conexão.

## Resultados

Cada execução de `tools\validate.ps1` grava o resultado em `artifacts\validation-log.txt`, e o GitHub Actions roda build e testes a cada push (`.github/workflows/build.yml`). Até 15/10/2026, contra um MinIO real rodou apenas o teste de conexão, que é somente leitura (resultado: conectado); nenhuma operação de escrita foi executada num servidor real, e a AWS não foi testada.
