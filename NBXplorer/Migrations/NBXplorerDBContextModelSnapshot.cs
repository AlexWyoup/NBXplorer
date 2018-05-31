﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NBXplorer.DB;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace NBXplorer.Migrations
{
    [DbContext(typeof(NBXplorerDBContext))]
    partial class NBXplorerDBContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn)
                .HasAnnotation("ProductVersion", "2.1.0-rc1-32029")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            modelBuilder.Entity("NBXplorer.DB.GenericTable", b =>
                {
                    b.Property<string>("PartitionKeyRowKey")
                        .ValueGeneratedOnAdd();

                    b.Property<byte[]>("Value");

                    b.HasKey("PartitionKeyRowKey");

                    b.ToTable("GenericTables");
                });
#pragma warning restore 612, 618
        }
    }
}
