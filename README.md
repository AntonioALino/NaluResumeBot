# 📚 NaluResumeBot

> Bot de estudos inteligente para processamento automático de PDFs via Discord: resumos, flashcards, quiz e mapas mentais gerados por IA.

---

## 🎯 Visão Geral

A **Nalu** manda PDF. O bot processa. Ela estuda. Simples assim.

O NaluResumeBot é uma automação que elimina o atrito de usar ferramentas de IA manualmente para estudar. Quando um PDF é enviado num canal do Discord, o sistema automaticamente gera material de estudo completo: resumo estruturado, flashcards, quiz e mapa mental tudo sem intervenção humana.

---

## 🏗️ Arquitetura

### Visão de Alto Nível

```
Discord ──upload PDF──► Discord Worker (Python)
                              │
                              ▼
                        RabbitMQ (fila de jobs)
                              │
                    ┌─────────┴──────────┐
                    ▼                    ▼
             PDF Worker (Python)    IA Worker (Python)
             (pdfplumber)           (Gemini API)
                    │                    │
                    └─────────┬──────────┘
                              ▼
                      Orquestrador (.NET 10)
                              │
                    ┌─────────┴──────────┐
                    ▼                    ▼
                Postgres              Redis
             (persistência)          (cache/sessões)
                              │
                              ▼
                    Discord Worker (Python)
                    (envia resposta formatada)
```

### Componentes

| Componente | Tecnologia | Responsabilidade |
|---|---|---|
| **Orquestrador** | .NET 10 / ASP .NET Core | API central, lógica de negócio, roteamento de jobs |
| **Discord Worker** | Python 3.12 + discord .py | Receber PDFs, enviar respostas formatadas |
| **PDF Worker** | Python 3.12 + pdfplumber | Extração e chunking de texto de PDFs |
| **IA Worker** | Python 3.12 + Gemini SDK | Geração de resumos, flashcards, quiz, mapas mentais |
| **Mensageria** | RabbitMQ | Desacoplamento entre workers, filas de processamento |
| **Banco Relacional** | PostgreSQL | Documentos, flashcards, histórico, usuários |
| **Cache** | Redis | Sessões de chat contextual, rate limiting, jobs em andamento |
| **Web App** | (pós-POC) | Visualização de mapas mentais, flashcards interativos |

---

## 🚀 POC — Escopo Mínimo Viável

O objetivo da POC é **validar o loop core** o mais rápido possível:

> PDF enviado no Discord → IA processa → resposta útil volta no chat

### Features da POC

- [x] Receber PDFs via Discord (canal dedicado)
- [x] Extrair texto do PDF com `pdfplumber`
- [x] Gerar **resumo estruturado** com Gemini
- [x] Gerar **flashcards** no chat (frente/verso em embed do Discord)
- [x] Gerar **mapa mental** em formato Markdown (markmap-compatible)

### Fora do escopo da POC

- Interface web
- Autenticação
- Multi-usuário
- Persistência avançada
- Spaced repetition engine

---

## 🗂️ Estrutura do Projeto

```
NaluResumeBot/
├── src/
│   ├── NaluResumeBot.Api/             # Orquestrador .NET 10
│   │   ├── Controllers/
│   │   │   └── JobsController.cs
│   │   ├── Services/
│   │   │   ├── JobDispatcherService.cs
│   │   │   └── DocumentService.cs
│   │   ├── Models/
│   │   └── Program.cs
│   │
│   └── workers/                       # Workers Python
│       ├── discord_worker/
│       │   ├── bot.py                 # Entry point do bot Discord
│       │   ├── handlers/
│       │   │   └── pdf_handler.py     # Lida com uploads de PDF
│       │   └── formatters/
│       │       └── embed_builder.py   # Formata respostas pro Discord
│       │
│       ├── pdf_worker/
│       │   ├── worker.py              # Consome fila do RabbitMQ
│       │   ├── extractor.py           # pdfplumber: extração de texto
│       │   └── chunker.py             # Chunking inteligente por seções
│       │
│       └── ai_worker/
│           ├── worker.py              # Consome fila do RabbitMQ
│           ├── gemini_client.py       # Wrapper do Gemini SDK
│           └── prompts/
│               ├── summary.py
│               ├── flashcards.py
│               ├── quiz.py
│               └── mindmap.py
│
├── infra/
│   ├── docker-compose.yml             # Postgres + Redis + RabbitMQ local
│   └── migrations/                    # SQL migrations
│
├── docs/
│   └── architecture.md
│
├── .env.example
└── README.md
```

---

## ⚙️ Como Rodar Localmente

### Pré-requisitos

- Docker e Docker Compose
- .NET 10 SDK
- Python 3.12+
- Conta no [Discord Developer Portal](https://discord.com/developers)
- API Key do Gemini (Google AI Studio)

### 1. Suba a infra local

```bash
docker compose -f infra/docker-compose.yml up -d
```

Isso sobe Postgres, Redis e RabbitMQ com configurações default.

### 2. Configure as variáveis de ambiente

```bash
cp .env.example .env
# Edite .env com suas credenciais
```

```env
# Discord
DISCORD_BOT_TOKEN=
DISCORD_CHANNEL_ID=

# Gemini
GEMINI_API_KEY=

# Banco
POSTGRES_CONNECTION_STRING=Host=localhost;Database=nalubot;Username=postgres;Password=postgres

# Redis
REDIS_CONNECTION_STRING=localhost:6379

# RabbitMQ
RABBITMQ_HOST=localhost
RABBITMQ_USER=guest
RABBITMQ_PASSWORD=guest
```

### 3. Suba o Orquestrador .NET

```bash
cd src/NaluResumeBot.Api
dotnet run
```

### 4. Instale dependências Python e suba os workers

```bash
cd src/workers
pip install -r requirements.txt

# Em terminais separados:
python discord_worker/bot.py
python pdf_worker/worker.py
python ai_worker/worker.py
```

---

## 🔄 Fluxo de Processamento

```
1. Usuária envia PDF no canal #estudos do Discord
        │
2. discord_worker detecta o upload e faz download do arquivo
        │
3. discord_worker publica job em RabbitMQ: fila `pdf.extract`
        │
4. pdf_worker consome o job:
   - Extrai texto com pdfplumber
   - Faz chunking por seções/páginas
   - Publica resultado em: fila `ai.process`
        │
5. ai_worker consome o job:
   - Chama Gemini com prompts especializados
   - Gera: resumo, flashcards, mapa mental
   - Publica resultado em: fila `discord.respond`
        │
6. discord_worker consome resposta e envia embeds formatados no Discord
```

---

## 🗃️ Modelo de Dados (POC)

```sql
-- Documentos processados
CREATE TABLE documents (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    filename    TEXT NOT NULL,
    discord_msg_id TEXT,
    status      TEXT DEFAULT 'pending', -- pending | processing | done | error
    created_at  TIMESTAMPTZ DEFAULT NOW()
);

-- Resumos gerados
CREATE TABLE summaries (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id UUID REFERENCES documents(id),
    content     TEXT NOT NULL,
    created_at  TIMESTAMPTZ DEFAULT NOW()
);

-- Flashcards
CREATE TABLE flashcards (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id UUID REFERENCES documents(id),
    front       TEXT NOT NULL,
    back        TEXT NOT NULL,
    created_at  TIMESTAMPTZ DEFAULT NOW()
);
```

---

## 🗺️ Roadmap Pós-POC

Funcionalidades planejadas para adicionar progressivamente após validação:

### Fase 2 — Engajamento
- **Quiz inteligente** — Perguntas de múltipla escolha geradas por IA com feedback por questão
- **Chat contextual** — Tirar dúvidas sobre o PDF enviado usando RAG (Retrieval-Augmented Generation)
- **Interface Web** — Visualização de mapas mentais com markmap.js, flashcards interativos

### Fase 3 — Revisão e Retenção
- **Spaced repetition** — Motor de repetição espaçada nos flashcards (algoritmo SM-2)
- **Exportação Anki** — Exportar flashcards no formato `.apkg` para importar no Anki
- **Progresso** — Dashboard de acompanhamento de revisões e desempenho nos quizzes

### Fase 4 — Produtividade Avançada
- **Resumo de áudio (TTS)** — Gerar podcast do resumo para ouvir enquanto se locomove
- **Plano de estudos** — Geração automática de cronograma de revisão baseado na data da prova
- **Comparação de documentos** — Identificar diferenças/evoluções entre versões de apostilas
- **Multi-PDF** — Consolidar vários PDFs de uma mesma matéria em material unificado

### Fase 5 — Infraestrutura
- **Background jobs .NET** — Migrar orquestração de workers para Hangfire ou .NET Worker Services
- **Multi-usuário** — Suporte a múltiplas pessoas com histórico isolado
- **Autenticação** — Login via Discord OAuth

---

## 🤔 Decisões de Arquitetura

### Por que Discord?
Canal de comunicação já usado no dia a dia. Discord tem API oficial, bots nativos, suporte a Markdown nos embeds, e upload de arquivos sem limitações absurdas. Sem risco de banimento (diferente de automações WhatsApp).

### Por que .NET como Orquestrador e Python nos Workers?
.NET é a stack principal e onde ficará a lógica de negócio, persistência e APIs. Python é mais ergonômico para integrar com SDKs de IA e bibliotecas de processamento de PDF (pdfplumber, LangChain). A separação via RabbitMQ mantém cada responsabilidade na linguagem certa.

### Por que RabbitMQ?
Desacopla o recebimento do PDF do processamento. Se o worker de IA estiver lento, os jobs ficam na fila sem bloquear o bot. Também facilita retry automático e escalabilidade horizontal (mais workers de IA rodando em paralelo).

### Por que Redis além do Postgres?
Postgres para persistência durável. Redis para estado efêmero: sessões de chat contextual (histórico da conversa com o PDF), rate limiting por usuário, e status de jobs em andamento para o bot dar feedback em tempo real ("⏳ Processando seu PDF...").

---

## 📋 Variáveis de Ambiente

| Variável | Descrição | Obrigatório |
|---|---|---|
| `DISCORD_BOT_TOKEN` | Token do bot no Discord Developer Portal | ✅ |
| `DISCORD_CHANNEL_ID` | ID do canal onde o bot monitora uploads | ✅ |
| `GEMINI_API_KEY` | Chave de API do Google Gemini | ✅ |
| `POSTGRES_CONNECTION_STRING` | String de conexão do PostgreSQL | ✅ |
| `REDIS_CONNECTION_STRING` | String de conexão do Redis | ✅ |
| `RABBITMQ_HOST` | Host do RabbitMQ | ✅ |
| `RABBITMQ_USER` | Usuário do RabbitMQ | ✅ |
| `RABBITMQ_PASSWORD` | Senha do RabbitMQ | ✅ |
| `MAX_PDF_SIZE_MB` | Tamanho máximo de PDF aceito (default: 20) | ❌ |
| `GEMINI_MODEL` | Modelo Gemini a usar (default: gemini-1.5-flash) | ❌ |

---

## 🤝 Contribuindo

Projeto pessoal/privado por enquanto. Se você chegou até aqui, provavelmente é a Nalu tentando entender o que esse negócio faz. 💜

---

*Feito com carinho para eliminar o atrito de estudar.*