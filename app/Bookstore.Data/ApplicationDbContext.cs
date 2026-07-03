using System;
using System.ComponentModel.DataAnnotations.Schema;
using Bookstore.Domain.Addresses;
using Bookstore.Domain.Books;
using Bookstore.Domain.Carts;
using Bookstore.Domain.Customers;
using Bookstore.Domain.Offers;
using Bookstore.Domain.Orders;
using Bookstore.Domain.ReferenceData;
using System.Data.Entity;
using System.Data.Entity.ModelConfiguration.Conventions;
using Npgsql;

namespace Bookstore.Data
{
    public class ApplicationDbContextPostgreSqlConfiguration : DbConfiguration
    {
        public ApplicationDbContextPostgreSqlConfiguration()
        {
            SetProviderServices("Npgsql", NpgsqlServices.Instance);
            SetDefaultConnectionFactory(new NpgsqlConnectionFactory());
        }
    }

    [DbConfigurationType(typeof(ApplicationDbContextPostgreSqlConfiguration))]
    public class ApplicationDbContext : DbContext
    {
        static ApplicationDbContext()
        {
            //Database.SetInitializer(new BookstoreDbInitializer());
            Database.SetInitializer<ApplicationDbContext>(null);
        }

        public ApplicationDbContext(string connectionString) : base(connectionString) { }

        public DbSet<Address> Address { get; set; }

        public DbSet<Book> Book { get; set; }

        public DbSet<Customer> Customer { get; set; }

        public DbSet<Order> Order { get; set; }

        public DbSet<ShoppingCart> ShoppingCart { get; set; }

        public DbSet<OrderItem> OrderItem { get; set; }

        public DbSet<Offer> Offer { get; set; }

        public DbSet<ReferenceDataItem> ReferenceData { get; set; }

        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            // Update to remove the pluralization to match the modern version
            modelBuilder.Conventions.Remove<PluralizingTableNameConvention>();

            // Address
            {
                var entity = modelBuilder.Entity<Address>();
                entity.ToTable("address", "bookstoreclassic_dbo");
                entity.Property(e => e.AddressLine1).HasColumnName("addressline1");
                entity.Property(e => e.AddressLine2).HasColumnName("addressline2");
                entity.Property(e => e.City).HasColumnName("city");
                entity.Property(e => e.State).HasColumnName("state");
                entity.Property(e => e.Country).HasColumnName("country");
                entity.Property(e => e.ZipCode).HasColumnName("zipcode");
                entity.Property(e => e.CustomerId).HasColumnName("customerid");
                entity.Property(e => e.IsActive).HasColumnName("isactive");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CreatedBy).HasColumnName("createdby");
                entity.Property(e => e.CreatedOn).HasColumnName("createdon");
                entity.Property(e => e.UpdatedOn).HasColumnName("updatedon");
            }

            // Book
            {
                var entity = modelBuilder.Entity<Book>();
                entity.ToTable("book", "bookstoreclassic_dbo");
                entity.Property(e => e.Name).HasColumnName("name");
                entity.Property(e => e.Author).HasColumnName("author");
                entity.Property(e => e.Year).HasColumnName("year");
                entity.Property(e => e.ISBN).HasColumnName("isbn");
                entity.Property(e => e.PublisherId).HasColumnName("publisherid");
                entity.Property(e => e.BookTypeId).HasColumnName("booktypeid");
                entity.Property(e => e.GenreId).HasColumnName("genreid");
                entity.Property(e => e.ConditionId).HasColumnName("conditionid");
                entity.Property(e => e.CoverImageUrl).HasColumnName("coverimageurl");
                entity.Property(e => e.Summary).HasColumnName("summary");
                entity.Property(e => e.Price).HasColumnName("price");
                entity.Property(e => e.Quantity).HasColumnName("quantity");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CreatedBy).HasColumnName("createdby");
                entity.Property(e => e.CreatedOn).HasColumnName("createdon");
                entity.Property(e => e.UpdatedOn).HasColumnName("updatedon");
                entity.HasRequired(x => x.Publisher).WithMany().HasForeignKey(x => x.PublisherId).WillCascadeOnDelete(false);
                entity.HasRequired(x => x.BookType).WithMany().HasForeignKey(x => x.BookTypeId).WillCascadeOnDelete(false);
                entity.HasRequired(x => x.Genre).WithMany().HasForeignKey(x => x.GenreId).WillCascadeOnDelete(false);
                entity.HasRequired(x => x.Condition).WithMany().HasForeignKey(x => x.ConditionId).WillCascadeOnDelete(false);
            }

            // Customer
            {
                var entity = modelBuilder.Entity<Customer>();
                entity.ToTable("customer", "bookstoreclassic_dbo");
                entity.Property(e => e.Sub).HasColumnName("sub").HasColumnType("varchar").HasMaxLength(450);
                entity.Property(e => e.Username).HasColumnName("username");
                entity.Property(e => e.FirstName).HasColumnName("firstname");
                entity.Property(e => e.LastName).HasColumnName("lastname");
                entity.Property(e => e.Email).HasColumnName("email");
                entity.Property(e => e.DateOfBirth).HasColumnName("dateofbirth");
                entity.Property(e => e.Phone).HasColumnName("phone");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CreatedBy).HasColumnName("createdby");
                entity.Property(e => e.CreatedOn).HasColumnName("createdon");
                entity.Property(e => e.UpdatedOn).HasColumnName("updatedon");
                entity.HasIndex(x => x.Sub).IsUnique();
            }

            // Offer
            {
                var entity = modelBuilder.Entity<Offer>();
                entity.ToTable("offer", "bookstoreclassic_dbo");
                entity.Property(e => e.Author).HasColumnName("author");
                entity.Property(e => e.ISBN).HasColumnName("isbn");
                entity.Property(e => e.BookName).HasColumnName("bookname");
                entity.Property(e => e.FrontUrl).HasColumnName("fronturl");
                entity.Property(e => e.GenreId).HasColumnName("genreid");
                entity.Property(e => e.ConditionId).HasColumnName("conditionid");
                entity.Property(e => e.PublisherId).HasColumnName("publisherid");
                entity.Property(e => e.BookTypeId).HasColumnName("booktypeid");
                entity.Property(e => e.Summary).HasColumnName("summary");
                entity.Property(e => e.OfferStatus).HasColumnName("offerstatus");
                entity.Property(e => e.Comment).HasColumnName("comment");
                entity.Property(e => e.CustomerId).HasColumnName("customerid");
                entity.Property(e => e.BookPrice).HasColumnName("bookprice");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CreatedBy).HasColumnName("createdby");
                entity.Property(e => e.CreatedOn).HasColumnName("createdon");
                entity.Property(e => e.UpdatedOn).HasColumnName("updatedon");
                entity.HasRequired(x => x.Publisher).WithMany().HasForeignKey(x => x.PublisherId).WillCascadeOnDelete(false);
                entity.HasRequired(x => x.BookType).WithMany().HasForeignKey(x => x.BookTypeId).WillCascadeOnDelete(false);
                entity.HasRequired(x => x.Genre).WithMany().HasForeignKey(x => x.GenreId).WillCascadeOnDelete(false);
                entity.HasRequired(x => x.Condition).WithMany().HasForeignKey(x => x.ConditionId).WillCascadeOnDelete(false);
            }

            // Order
            {
                var entity = modelBuilder.Entity<Order>();
                entity.ToTable("Order", "bookstoreclassic_dbo");
                entity.Property(e => e.CustomerId).HasColumnName("customerid");
                entity.Property(e => e.AddressId).HasColumnName("addressid");
                entity.Property(e => e.DeliveryDate).HasColumnName("deliverydate");
                entity.Property(e => e.OrderStatus).HasColumnName("orderstatus");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CreatedBy).HasColumnName("createdby");
                entity.Property(e => e.CreatedOn).HasColumnName("createdon");
                entity.Property(e => e.UpdatedOn).HasColumnName("updatedon");
                entity.HasRequired(x => x.Customer).WithMany().WillCascadeOnDelete(false);
            }

            // ShoppingCart
            {
                var entity = modelBuilder.Entity<ShoppingCart>();
                entity.ToTable("shoppingcart", "bookstoreclassic_dbo");
                entity.Property(e => e.CorrelationId).HasColumnName("correlationid");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CreatedBy).HasColumnName("createdby");
                entity.Property(e => e.CreatedOn).HasColumnName("createdon");
                entity.Property(e => e.UpdatedOn).HasColumnName("updatedon");
            }

            // OrderItem
            {
                var entity = modelBuilder.Entity<OrderItem>();
                entity.ToTable("orderitem", "bookstoreclassic_dbo");
                entity.Property(e => e.OrderId).HasColumnName("orderid");
                entity.Property(e => e.BookId).HasColumnName("bookid");
                entity.Property(e => e.Quantity).HasColumnName("quantity");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CreatedBy).HasColumnName("createdby");
                entity.Property(e => e.CreatedOn).HasColumnName("createdon");
                entity.Property(e => e.UpdatedOn).HasColumnName("updatedon");
            }

            // ReferenceData — entity class is ReferenceDataItem, mapped to table referencedata
            {
                var entity = modelBuilder.Entity<ReferenceDataItem>();
                entity.ToTable("referencedata", "bookstoreclassic_dbo");
                entity.Property(e => e.DataType).HasColumnName("datatype");
                entity.Property(e => e.Text).HasColumnName("text");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CreatedBy).HasColumnName("createdby");
                entity.Property(e => e.CreatedOn).HasColumnName("createdon");
                entity.Property(e => e.UpdatedOn).HasColumnName("updatedon");
            }

            // ShoppingCartItem — composite key and identity generation preserved
            modelBuilder.Entity<ShoppingCartItem>().HasKey(x => new { x.Id, x.ShoppingCartId });
            modelBuilder.Entity<ShoppingCartItem>().Property(x => x.Id).HasDatabaseGeneratedOption(DatabaseGeneratedOption.Identity);
        }
    }
}
